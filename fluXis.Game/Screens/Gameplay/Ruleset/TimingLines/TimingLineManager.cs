using System.Collections.Generic;
using System.Linq;
using fluXis.Game.Configuration;
using fluXis.Game.Map;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace fluXis.Game.Screens.Gameplay.Ruleset.TimingLines;

public partial class TimingLineManager : CompositeDrawable
{
    public HitObjectManager HitObjectManager { get; }

    private Bindable<bool> showTimingLines;

    private readonly List<TimingLine> timingLines = new();
    private readonly List<TimingLine> futureTimingLines = new();

    public TimingLineManager(HitObjectManager hitObjectManager)
    {
        HitObjectManager = hitObjectManager;
        RelativeSizeAxes = Axes.Y;
        Anchor = Anchor.Centre;
        Origin = Anchor.Centre;
    }

    [BackgroundDependencyLoader]
    private void load(FluXisConfig config)
    {
        showTimingLines = config.GetBindable<bool>(FluXisSetting.TimingLines);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        showTimingLines.BindValueChanged(e => this.FadeTo(e.NewValue ? 1 : 0, 400), true);
    }

    public void CreateLines(MapInfo map)
    {
        for (int i = 0; i < map.TimingPoints.Count; i++)
        {
            var point = map.TimingPoints[i];

            if (point.HideLines || point.Signature == 0)
                continue;

            float target = i + 1 < map.TimingPoints.Count ? map.TimingPoints[i + 1].Time : map.EndTime;
            float increase = point.Signature * point.MsPerBeat;
            float position = point.Time;

            while (position < target)
            {
                futureTimingLines.Add(new TimingLine(this, HitObjectManager.PositionFromTime(position)));
                position += increase;
            }
        }

        futureTimingLines.Sort((a, b) => a.ScrollVelocityTime.CompareTo(b.ScrollVelocityTime));
    }

    protected override void Update()
    {
        while (futureTimingLines is { Count: > 0 } && futureTimingLines[0].ScrollVelocityTime <= HitObjectManager.CurrentTime + 2000 * HitObjectManager.Playfield.Screen.Rate / HitObjectManager.ScrollSpeed)
        {
            TimingLine line = futureTimingLines[0];
            futureTimingLines.RemoveAt(0);
            timingLines.Add(line);
            AddInternal(line);
        }

        foreach (var line in timingLines.Where(t => t.Y > DrawHeight).ToArray())
        {
            timingLines.Remove(line);
            RemoveInternal(line, true);
        }

        Width = HitObjectManager.Playfield.Stage.Width;
        base.Update();
    }
}
