using System;
using System.Collections.Generic;
using System.Linq;
using fluXis.Game.Map;
using fluXis.Game.Overlay.Mouse;
using fluXis.Game.Screens.Edit.Tabs.Charting.Placement;
using fluXis.Game.Screens.Edit.Tabs.Charting.Selection;
using fluXis.Game.Screens.Edit.Tabs.Charting.Tools;
using fluXis.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Input;

namespace fluXis.Game.Screens.Edit.Tabs.Charting.Blueprints;

public partial class BlueprintContainer : Container, ICursorDrag
{
    [Resolved]
    private EditorValues values { get; set; }

    public ChartingContainer ChartingContainer { get; init; }

    public ChartingTool CurrentTool
    {
        get => currentTool;
        set
        {
            var previousTool = currentTool;
            currentTool = value;
            removePlacement();

            CurrentToolChanged?.Invoke(previousTool, currentTool);
        }
    }

    public event Action<ChartingTool, ChartingTool> CurrentToolChanged;

    private ChartingTool currentTool;

    protected readonly BindableList<HitObjectInfo> SelectedHitObjects = new();

    public SelectionBox SelectionBox { get; private set; }
    public SelectionBlueprints SelectionBlueprints { get; private set; }
    public SelectionHandler SelectionHandler { get; private set; }

    private InputManager inputManager;
    private MouseButtonEvent lastDragEvent;
    private readonly Dictionary<HitObjectInfo, SelectionBlueprint> blueprints = new();

    // movement
    private bool isDragging;
    private SelectionBlueprint[] dragBlueprints;
    private Vector2[] dragBlueprintsPositions;

    private PlacementBlueprint currentPlacementBlueprint;
    private Container placementBlueprintContainer;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;
        currentTool = ChartingContainer.Tools[0] as SelectTool;

        SelectionHandler = new SelectionHandler();
        SelectionHandler.SelectedHitObjects.BindTo(SelectedHitObjects);

        InternalChildren = new Drawable[]
        {
            SelectionBox = new SelectionBox { Playfield = ChartingContainer.Playfield },
            SelectionHandler,
            SelectionBlueprints = new SelectionBlueprints(),
            placementBlueprintContainer = new Container { RelativeSizeAxes = Axes.Both }
        };
    }

    protected override void LoadComplete()
    {
        inputManager = GetContainingInputManager();

        if (ChartingContainer == null) return;

        values.MapInfo.HitObjectAdded += AddBlueprint;
        values.MapInfo.HitObjectRemoved += RemoveBlueprint;

        foreach (var hitObject in ChartingContainer.HitObjects)
            AddBlueprint(hitObject.Data);
    }

    protected override bool OnDragStart(DragStartEvent e)
    {
        if (e.Button != MouseButton.Left) return false;

        lastDragEvent = e;

        if (dragBlueprints != null)
        {
            isDragging = true;
            return true;
        }

        SelectionBox.HandleDrag(e);
        SelectionBox.Show();
        return true;
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        return selectByClick(e) || prepareMovement();
    }

    protected override void OnDrag(DragEvent e)
    {
        lastDragEvent = e;

        if (isDragging)
            moveCurrentSelection(e);
    }

    protected override void OnDragEnd(DragEndEvent e)
    {
        lastDragEvent = null;
        isDragging = false;
        dragBlueprints = null;
        SelectionBox.Hide();
    }

    public void AddBlueprint(HitObjectInfo info)
    {
        if (blueprints.ContainsKey(info))
            return;

        var blueprint = createBlueprint(info);
        blueprints[info] = blueprint;
        blueprint.Selected += onSelected;
        blueprint.Deselected += onDeselected;
        SelectionBlueprints.Add(blueprint);
    }

    public void RemoveBlueprint(HitObjectInfo info)
    {
        if (!blueprints.Remove(info, out var blueprint))
            return;

        blueprint.Deselect();
        blueprint.Selected -= onSelected;
        blueprint.Deselected -= onDeselected;
        SelectionBlueprints.Remove(blueprint, true);
    }

    private SelectionBlueprint createBlueprint(HitObjectInfo info)
    {
        var drawable = ChartingContainer.HitObjects.FirstOrDefault(d => d.Data == info);

        if (drawable == null) return null;

        SelectionBlueprint blueprint = info.IsLongNote() ? new LongNoteSelectionBlueprint(info) : new SingleNoteSelectionBlueprint(info);
        blueprint.Drawable = drawable;
        return blueprint;
    }

    private bool selectByClick(MouseButtonEvent e)
    {
        foreach (SelectionBlueprint blueprint in SelectionBlueprints.AliveChildren.Reverse().OrderByDescending(b => b.IsSelected))
        {
            if (!blueprint.IsHovered) continue;

            return SelectionHandler.SingleClickSelection(blueprint, e);
        }

        return false;
    }

    protected override void Update()
    {
        base.Update();

        if (lastDragEvent != null && SelectionBox.State == Visibility.Visible)
        {
            lastDragEvent.Target = this;
            SelectionBox.HandleDrag(lastDragEvent);
            UpdateSelection();
        }

        if (currentPlacementBlueprint != null)
        {
            switch (currentPlacementBlueprint.State)
            {
                case PlacementState.Waiting:
                    if (!ChartingContainer.CursorInPlacementArea)
                        removePlacement();
                    break;

                case PlacementState.Completed:
                    removePlacement();
                    break;
            }
        }

        if (ChartingContainer.CursorInPlacementArea)
            ensurePlacementCreated();

        if (currentPlacementBlueprint != null)
            updatePlacementPosition();
    }

    private void ensurePlacementCreated()
    {
        if (currentPlacementBlueprint != null) return;

        var blueprint = CurrentTool?.CreateBlueprint();

        if (blueprint != null)
        {
            placementBlueprintContainer.Child = currentPlacementBlueprint = blueprint;
            // updatePlacementPosition();
        }
    }

    private void updatePlacementPosition()
    {
        var hitObjectContainer = ChartingContainer.Playfield.HitObjectContainer;
        var mousePosition = inputManager.CurrentState.Mouse.Position;

        var time = hitObjectContainer.SnapTime(hitObjectContainer.TimeAtScreenSpacePosition(mousePosition));
        var lane = hitObjectContainer.LaneAtScreenSpacePosition(mousePosition);
        currentPlacementBlueprint.UpdatePlacement(time, lane);
    }

    private void removePlacement()
    {
        currentPlacementBlueprint?.EndPlacement(false);
        currentPlacementBlueprint?.Expire();
        currentPlacementBlueprint = null;
    }

    public void UpdateSelection()
    {
        var quad = SelectionBox.Box.ScreenSpaceDrawQuad;

        foreach (var blueprint in SelectionBlueprints)
        {
            switch (blueprint.State)
            {
                case SelectedState.Selected:
                    if (!quad.Contains(blueprint.ScreenSpaceSelectionPoint))
                        blueprint.Deselect();
                    break;

                case SelectedState.Deselected:
                    if (blueprint.IsAlive && blueprint.IsPresent && quad.Contains(blueprint.ScreenSpaceSelectionPoint))
                        blueprint.Select();
                    break;
            }
        }
    }

    public void SelectAll()
    {
        foreach (var blueprint in SelectionBlueprints)
            blueprint.Select();
    }

    private void onSelected(SelectionBlueprint blueprint)
    {
        SelectionHandler.HandleSelection(blueprint);
    }

    private void onDeselected(SelectionBlueprint blueprint)
    {
        SelectionHandler.HandleDeselection(blueprint);
    }

    private bool prepareMovement()
    {
        if (!SelectionHandler.Selected.Any())
            return false;

        if (!SelectionHandler.Selected.Any(b => b.IsHovered))
            return false;

        dragBlueprints = SelectionHandler.Selected.ToArray();
        dragBlueprintsPositions = dragBlueprints.Select(m => m.ScreenSpaceSelectionPoint).ToArray();
        return true;
    }

    private void moveCurrentSelection(DragEvent e)
    {
        if (dragBlueprints == null) return;

        Vector2 delta = e.ScreenSpaceMousePosition - e.ScreenSpaceMouseDownPosition;

        Vector2 postition = dragBlueprintsPositions.First() + delta;
        float time = ChartingContainer.Playfield.HitObjectContainer.TimeAtScreenSpacePosition(postition);
        int lane = ChartingContainer.Playfield.HitObjectContainer.LaneAtScreenSpacePosition(postition);
        float snappedTime = ChartingContainer.Playfield.HitObjectContainer.SnapTime(time);

        float timeDelta = snappedTime - dragBlueprints.First().HitObject.Time;
        int laneDelta = lane - dragBlueprints.First().HitObject.Lane;

        var minLane = dragBlueprints.Min(b => b.HitObject.Lane);
        var maxLane = dragBlueprints.Max(b => b.HitObject.Lane);

        if (minLane + laneDelta <= 0 || maxLane + laneDelta > values.MapInfo.KeyCount)
            laneDelta = 0;

        foreach (var blueprint in dragBlueprints)
        {
            blueprint.HitObject.Time += timeDelta;
            blueprint.HitObject.Lane += laneDelta;
        }
    }
}
