﻿using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Helpers;
using Random_Features.Libs;
using SharpDX;
using nuVector2 = System.Numerics.Vector2;

// ReSharper disable ConstantConditionalAccessQualifier

namespace PickIt
{
    public class PickIt : BaseSettingsPlugin<PickItSettings>
    {
        private readonly Stopwatch _debugTimer = Stopwatch.StartNew();
        private readonly WaitTime _toPick = new WaitTime(1);
        private readonly WaitTime _wait2Ms = new WaitTime(2);
        private Vector2 _clickWindowOffset;
        private bool _enabled;
        private readonly Dictionary<string, int> _weightsRules = new Dictionary<string, int>();
        private WaitTime _workCoroutine;
        private uint _coroutineCounter;
        private bool _fullWork = true;
        private Coroutine _pickItCoroutine;
        private TimeCache<List<CustomItem>> _currentLabels;

        public override bool Initialise()
        {
            _currentLabels = new TimeCache<List<CustomItem>>(UpdateCurrentLabels, 500);

            #region Register keys

            Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
            Input.RegisterKey(Settings.PickUpKey);
            Input.RegisterKey(Keys.Escape);

            #endregion

            _pickItCoroutine = new Coroutine(MainWorkCoroutine(), this, "Pick It");
            Core.ParallelRunner.Run(_pickItCoroutine);
            _pickItCoroutine.Pause();
            _debugTimer.Reset();
            _workCoroutine = new WaitTime(Settings.ExtraDelay);
            Settings.ExtraDelay.OnValueChanged += (sender, i) => _workCoroutine = new WaitTime(i);
            return true;
        }

        private IEnumerator MainWorkCoroutine()
        {
            while (true)
            {
                yield return FindItemToPick();
                _coroutineCounter++;
                _pickItCoroutine.UpdateTicks(_coroutineCounter);
                yield return _workCoroutine;
            }
            // ReSharper disable once IteratorNeverReturns
        }

        public override void DrawSettings()
        {
            Settings.PickUpKey =
                ImGuiExtension.HotkeySelector("Pickup Key: " + Settings.PickUpKey.Value, Settings.PickUpKey);
            Settings.PickupRange.Value = ImGuiExtension.IntSlider("Pickup Radius", Settings.PickupRange);
            Settings.ExtraDelay.Value = ImGuiExtension.IntSlider("Extra Click Delay", Settings.ExtraDelay);
            Settings.TimeBeforeNewClick.Value =
                ImGuiExtension.IntSlider("Time wait for new click", Settings.TimeBeforeNewClick);
        }

        public override Job Tick()
        {
            if (Input.GetKeyState(Keys.Escape))
            {
                _enabled = false;
                _pickItCoroutine.Pause();
            }

            if (_enabled || Input.GetKeyState(Settings.PickUpKey.Value))
            {
                _debugTimer.Restart();

                if (_pickItCoroutine.IsDone)
                {
                    var firstOrDefault =
                        Core.ParallelRunner.Coroutines.FirstOrDefault(x => x.OwnerName == nameof(PickIt));

                    if (firstOrDefault != null)
                        _pickItCoroutine = firstOrDefault;
                }

                _pickItCoroutine.Resume();
                _fullWork = false;
            }
            else
            {
                if (_fullWork)
                {
                    _pickItCoroutine.Pause();
                    _debugTimer.Reset();
                }
            }

            if (_debugTimer.ElapsedMilliseconds > 300)
            {
                _fullWork = true;
                _debugTimer.Reset();
            }

            return null;
        }

        public override void ReceiveEvent(string eventId, object args)
        {
            if (eventId == "start_pick_it") _enabled = true;
            if (eventId == "end_pick_it") _enabled = false;
        }

        private List<CustomItem> UpdateCurrentLabels()
        {
            const string morphPath = "Metadata/MiscellaneousObjects/Metamorphosis/MetamorphosisMonsterMarker";
            var window = GameController.Window.GetWindowRectangleTimeCache;
            var rect = new RectangleF(window.X, window.X, window.X + window.Width, window.Y + window.Height);
            var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                    x.ItemOnGround?.Path != null &&
                    x.IsVisible && x.Label.GetClientRectCache.Center.PointInRectangle(rect) &&
                    x.CanPickUp && x.MaxTimeForPickUp.TotalSeconds <= 0 || x.ItemOnGround?.Path == morphPath)
                .Select(x => new CustomItem(x, GameController.Files,
                    x.ItemOnGround.DistancePlayer, _weightsRules, x.ItemOnGround?.Path == morphPath))
                .OrderBy(x => x.Distance).ToList();
            return labels;
        }

        private IEnumerator FindItemToPick()
        {
            if (!GameController.Window.IsForeground()) yield break;
            var portalLabel = GetLabel(@"Metadata/MiscellaneousObjects/MultiplexPortal");
            var rectangleOfGameWindow = GameController.Window.GetWindowRectangleTimeCache;
            rectangleOfGameWindow.Inflate(-36, -36);
            var pickUpThisItem = _currentLabels.Value.FirstOrDefault(x =>
                x.Distance < Settings.PickupRange && x.GroundItem != null &&
                rectangleOfGameWindow.Intersects(new RectangleF(x.LabelOnGround.Label.GetClientRectCache.Center.X,
                    x.LabelOnGround.Label.GetClientRectCache.Center.Y, 3, 3)));
            if (_enabled || Input.GetKeyState(Settings.PickUpKey.Value))
            {
                yield return TryToPickV2(pickUpThisItem, portalLabel);
                _fullWork = true;
            }
        }

        private IEnumerator TryToPickV2(CustomItem pickItItem, LabelOnGround portalLabel)
        {
            if (!pickItItem.IsValid)
            {
                _fullWork = true;
                //LogMessage("PickItem is not valid.", 5, Color.Red);
                yield break;
            }

            var centerOfItemLabel = pickItItem.LabelOnGround.Label.GetClientRectCache.Center;
            var rectangleOfGameWindow = GameController.Window.GetWindowRectangleTimeCache;

            _clickWindowOffset = rectangleOfGameWindow.TopLeft;
            rectangleOfGameWindow.Inflate(-36, -36);
            centerOfItemLabel.X += rectangleOfGameWindow.Left;
            centerOfItemLabel.Y += rectangleOfGameWindow.Top;
            if (!rectangleOfGameWindow.Intersects(new RectangleF(centerOfItemLabel.X, centerOfItemLabel.Y, 3, 3)))
            {
                _fullWork = true;
                //LogMessage($"Label outside game window. Label: {centerOfItemLabel} Window: {rectangleOfGameWindow}", 5, Color.Red);
                yield break;
            }

            var tryCount = 0;

            while (tryCount < 3)
            {
                var completeItemLabel = pickItItem.LabelOnGround?.Label;

                if (completeItemLabel == null)
                {
                    if (tryCount > 0)
                        //LogMessage("Probably item already picked.", 3);
                        yield break;

                    //LogError("Label for item not found.", 5);
                    yield break;
                }

                Vector2 vector2;
                if (IsPortalNearby(portalLabel, pickItItem.LabelOnGround))
                    vector2 = completeItemLabel.GetClientRect().ClickRandom() + _clickWindowOffset;
                else
                    vector2 = completeItemLabel.GetClientRect().Center + _clickWindowOffset;

                if (!rectangleOfGameWindow.Intersects(new RectangleF(vector2.X, vector2.Y, 3, 3)))
                {
                    _fullWork = true;
                    //LogMessage($"x,y outside game window. Label: {centerOfItemLabel} Window: {rectangleOfGameWindow}", 5, Color.Red);
                    yield break;
                }

                Input.SetCursorPos(vector2);
                yield return _wait2Ms;

                if (pickItItem.IsTargeted())
                {
                    // in case of portal nearby do extra checks with delays
                    if (IsPortalNearby(portalLabel, pickItItem.LabelOnGround) && !IsPortalTargeted(portalLabel))
                    {
                        yield return new WaitTime(25);
                        if (IsPortalNearby(portalLabel, pickItItem.LabelOnGround) && !IsPortalTargeted(portalLabel))
                            Input.Click(MouseButtons.Left);
                    }
                    else if (!IsPortalNearby(portalLabel, pickItItem.LabelOnGround))
                    {
                        Input.Click(MouseButtons.Left);
                    }
                }

                yield return _toPick;
                tryCount++;
            }

            tryCount = 0;

            while (GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelsVisible.FirstOrDefault(
                x => x.Address == pickItItem.LabelOnGround.Address) != null && tryCount < 6)
                tryCount++;
        }

        private bool IsPortalTargeted(LabelOnGround portalLabel)
        {
            return GameController.IngameState.UIHoverElement.Address ==
                   portalLabel.Label.Address || // this is the right one
                   portalLabel?.ItemOnGround?.HasComponent<Targetable>() == true &&
                   portalLabel?.ItemOnGround?.GetComponent<Targetable>()?.isTargeted == true;
        }

        private bool IsPortalNearby(LabelOnGround portalLabel, LabelOnGround pickItItem)
        {
            if (portalLabel == null || pickItItem == null) return false;
            var rect1 = portalLabel.Label.GetClientRectCache;
            var rect2 = pickItItem.Label.GetClientRectCache;
            rect1.Inflate(100, 100);
            rect2.Inflate(100, 100);
            return rect1.Intersects(rect2);
        }

        private LabelOnGround GetLabel(string id)
        {
            var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;

            var labelQuery =
                from labelOnGround in labels
                let label = labelOnGround?.Label
                where label?.IsValid == true &&
                      label?.Address > 0 &&
                      label?.IsVisible == true
                let itemOnGround = labelOnGround?.ItemOnGround
                where itemOnGround != null &&
                      itemOnGround?.Metadata?.Contains(id) == true
                let dist = GameController?.Player?.GridPos.DistanceSquared(itemOnGround.GridPos)
                orderby dist
                select labelOnGround;

            return labelQuery.FirstOrDefault();
        }

        public override void OnPluginDestroyForHotReload()
        {
            _pickItCoroutine.Done(true);
        }
    }
}