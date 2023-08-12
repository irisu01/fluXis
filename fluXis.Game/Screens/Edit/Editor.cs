using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using fluXis.Game.Activity;
using fluXis.Game.Audio;
using fluXis.Game.Database;
using fluXis.Game.Database.Maps;
using fluXis.Game.Graphics;
using fluXis.Game.Graphics.Background;
using fluXis.Game.Graphics.Context;
using fluXis.Game.Graphics.Panel;
using fluXis.Game.Input;
using fluXis.Game.Map;
using fluXis.Game.Online.API;
using fluXis.Game.Online.API.Maps;
using fluXis.Game.Online.Fluxel;
using fluXis.Game.Overlay.Notification;
using fluXis.Game.Screens.Edit.MenuBar;
using fluXis.Game.Screens.Edit.Tabs;
using fluXis.Game.Screens.Edit.TabSwitcher;
using fluXis.Game.Screens.Edit.Timeline;
using fluXis.Game.Utils;
using Newtonsoft.Json;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osuTK.Input;

namespace fluXis.Game.Screens.Edit;

public partial class Editor : FluXisScreen, IKeyBindingHandler<FluXisKeybind>
{
    public override bool ShowToolbar => false;
    public override float BackgroundDim => 0.4f;
    public override bool AllowMusicControl => false;

    [Resolved]
    private NotificationOverlay notifications { get; set; }

    [Resolved]
    private FluXisRealm realm { get; set; }

    [Resolved]
    private Storage storage { get; set; }

    [Resolved]
    private MapStore mapStore { get; set; }

    [Resolved]
    private BackgroundStack backgrounds { get; set; }

    [Resolved]
    private AudioClock audioClock { get; set; }

    [Resolved]
    private Fluxel fluxel { get; set; }

    [Resolved]
    private FluXisGameBase game { get; set; }

    [Resolved]
    private ActivityManager activity { get; set; }

    private ITrackStore trackStore { get; set; }

    public RealmMap Map;
    public EditorMapInfo MapInfo;

    private Container tabs;
    private int currentTab;

    private EditorMenuBar menuBar;
    private EditorTabSwitcher tabSwitcher;
    private EditorBottomBar bottomBar;

    private EditorClock clock;
    private Bindable<Waveform> waveform;
    private EditorChangeHandler changeHandler;
    private EditorValues values;

    private DependencyContainer dependencies;
    private bool exitConfirmed;
    private bool isNewMap;

    public Editor(RealmMap realmMap = null, MapInfo map = null)
    {
        isNewMap = map == null && realmMap == null;
        Map = realmMap ?? RealmMap.CreateNew();
        MapInfo = getEditorMapInfo(map) ?? new EditorMapInfo(new MapMetadata());
        MapInfo.KeyCount = Map.KeyCount;
    }

    [BackgroundDependencyLoader]
    private void load(AudioManager audioManager, Storage storage)
    {
        audioClock.Looping = false;
        audioClock.Stop(); // the editor clock will handle this

        backgrounds.AddBackgroundFromMap(Map);
        trackStore = audioManager.GetTrackStore(new StorageBackedResourceStore(storage.GetStorageForDirectory("files")));

        dependencies.CacheAs(waveform = new Bindable<Waveform>());
        dependencies.CacheAs(changeHandler = new EditorChangeHandler());
        dependencies.CacheAs(values = new EditorValues
        {
            MapInfo = MapInfo,
            MapEvents = MapInfo.MapEvents ?? new EditorMapEvents(),
            Editor = this
        });

        changeHandler.OnTimingPointAdded += () => Logger.Log("Timing point added");
        changeHandler.OnTimingPointRemoved += () => Logger.Log("Timing point removed");
        changeHandler.OnTimingPointChanged += () => Logger.Log("Timing point changed");

        clock = new EditorClock(MapInfo);
        clock.ChangeSource(loadMapTrack());
        dependencies.CacheAs(clock);

        InternalChildren = new Drawable[]
        {
            clock,
            new FluXisContextMenuContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new PopoverContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = tabs = new Container
                    {
                        Padding = new MarginPadding { Top = 45, Bottom = 60 },
                        RelativeSizeAxes = Axes.Both,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Children = new Drawable[]
                        {
                            new SetupTab(this),
                            new ChartingTab(this),
                            new TimingTab(this)
                        }
                    }
                }
            },
            menuBar = new EditorMenuBar
            {
                Items = new MenuItem[]
                {
                    new("File")
                    {
                        Items = new MenuItem[]
                        {
                            new("Save", () => save()),
                            new("Export", export),
                            new("Upload", startUpload),
                            new("Exit", tryExit)
                        }
                    },
                    new("Edit")
                    {
                        Items = new MenuItem[]
                        {
                            new("Undo", sendWipNotification),
                            new("Redo", sendWipNotification),
                            new("Cut", sendWipNotification),
                            new("Copy", sendWipNotification),
                            new("Paste", sendWipNotification),
                            new("Delete", sendWipNotification),
                            new("Select all", sendWipNotification)
                        }
                    },
                    new("View")
                    {
                        Items = new MenuItem[]
                        {
                            new("Waveform opacity")
                            {
                                Items = new MenuItem[]
                                {
                                    new("0%", () => values.WaveformOpacity.Value = 0),
                                    new("25%", () => values.WaveformOpacity.Value = 0.25f),
                                    new("50%", () => values.WaveformOpacity.Value = 0.5f),
                                    new("75%", () => values.WaveformOpacity.Value = 0.75f),
                                    new("100%", () => values.WaveformOpacity.Value = 1)
                                }
                            }
                        }
                    },
                    new("Timing")
                    {
                        Items = new MenuItem[]
                        {
                            new("Set preview point to current time", () =>
                            {
                                MapInfo.Metadata.PreviewTime = (int)clock.CurrentTime;
                                Map.Metadata.PreviewTime = (int)clock.CurrentTime;
                            })
                        }
                    }
                }
            },
            tabSwitcher = new EditorTabSwitcher
            {
                Children = new EditorTabSwitcherButton[]
                {
                    new()
                    {
                        Text = "Setup",
                        Action = () => changeTab(0)
                    },
                    new()
                    {
                        Text = "Charting",
                        Action = () => changeTab(1)
                    },
                    new()
                    {
                        Text = "Timing",
                        Action = () => changeTab(2)
                    }
                }
            },
            bottomBar = new EditorBottomBar()
        };
    }

    private Track loadMapTrack()
    {
        string path = Map.MapSet?.GetFile(Map.Metadata?.Audio)?.Path;

        Waveform w = null;

        if (!string.IsNullOrEmpty(path))
        {
            Stream s = trackStore.GetStream(path);
            if (s != null) w = new Waveform(s);
        }

        waveform.Value = w;
        return Map.GetTrack() ?? trackStore.GetVirtual(10000);
    }

    protected override void LoadComplete()
    {
        changeTab(isNewMap ? 0 : 1);
    }

    public void SortEverything()
    {
        MapInfo.HitObjects.Sort((a, b) => a.Time.CompareTo(b.Time));
        MapInfo.TimingPoints.Sort((a, b) => a.Time.CompareTo(b.Time));
        MapInfo.ScrollVelocities.Sort((a, b) => a.Time.CompareTo(b.Time));
        values.MapEvents.FlashEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
        values.MapEvents.LaneSwitchEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private void changeTab(int to)
    {
        currentTab = to;

        if (currentTab < 0)
            currentTab = 0;
        if (currentTab >= tabs.Count)
            currentTab = tabs.Count - 1;

        for (var i = 0; i < tabs.Children.Count; i++)
        {
            Drawable tab = tabs.Children[i];
            tab.FadeTo(i == currentTab ? 1 : 0);
        }
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.ControlPressed)
        {
            if (e.Key is >= Key.Number1 and <= Key.Number9)
            {
                int index = e.Key - Key.Number1;

                if (index < tabs.Count)
                    changeTab(index);

                return true;
            }

            switch (e.Key)
            {
                case Key.S:
                    save();
                    return true;
            }
        }

        return false;
    }

    public bool OnPressed(KeyBindingPressEvent<FluXisKeybind> e)
    {
        switch (e.Action)
        {
            case FluXisKeybind.Back:
                this.Exit();
                return true;
        }

        return false;
    }

    public void OnReleased(KeyBindingReleaseEvent<FluXisKeybind> e)
    {
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        if (HasChanges() && !exitConfirmed)
        {
            game.Overlay ??= new ButtonPanel
            {
                Text = "There are unsaved changes.\nAre you sure you want to exit?",
                Buttons = new ButtonData[]
                {
                    new()
                    {
                        Text = "Yes",
                        Action = () =>
                        {
                            exitConfirmed = true;
                            this.Exit();
                        }
                    },
                    new() { Text = "Nevermind" }
                }
            };

            return true;
        }

        exitAnimation();
        clock.Stop();
        audioClock.Seek((float)clock.CurrentTime);
        return false;
    }

    public override void OnEntering(ScreenTransitionEvent e) => enterAnimation();
    public override void OnResuming(ScreenTransitionEvent e) => enterAnimation();
    public override void OnSuspending(ScreenTransitionEvent e) => exitAnimation();

    private void exitAnimation()
    {
        this.FadeOut(200);
        menuBar.MoveToY(-menuBar.Height, 300, Easing.OutQuint);
        tabSwitcher.MoveToY(-menuBar.Height, 300, Easing.OutQuint);
        bottomBar.MoveToY(bottomBar.Height, 300, Easing.OutQuint);
        tabs.ScaleTo(.9f, 300, Easing.OutQuint);
    }

    private void enterAnimation()
    {
        this.FadeInFromZero(200);
        menuBar.MoveToY(0, 300, Easing.OutQuint);
        tabSwitcher.MoveToY(0, 300, Easing.OutQuint);
        bottomBar.MoveToY(0, 300, Easing.OutQuint);
        tabs.ScaleTo(1, 300, Easing.OutQuint);

        activity.Update("Editing a map", "", "editor");
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent) => dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

    private bool save(bool setStatus = true)
    {
        if (Map == null)
            return false;

        if (Map.Status >= 100)
        {
            notifications.PostError("Map is from another game!");
            return false;
        }

        if (MapInfo.HitObjects.Count == 0)
        {
            notifications.PostError("Map has no hit objects!");
            return false;
        }

        if (MapInfo.TimingPoints.Count == 0)
        {
            notifications.PostError("Map has no timing points!");
            return false;
        }

        MapInfo.Sort();

        string effects = values.MapEvents.Save();
        var effectsHash = MapUtils.GetHash(effects);
        var effectsPath = storage.GetFullPath($"files/{PathUtils.HashToPath(effectsHash)}");
        var effectsDir = Path.GetDirectoryName(effectsPath);

        if (!Directory.Exists(effectsDir))
            Directory.CreateDirectory(effectsDir);

        File.WriteAllText(effectsPath, effects);
        MapInfo.EffectFile = string.IsNullOrEmpty(MapInfo.EffectFile) ? $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.ffx" : MapInfo.EffectFile;

        // get map as json
        string json = JsonConvert.SerializeObject(MapInfo);
        string hash = MapUtils.GetHash(json);

        if (hash == Map.Hash)
        {
            notifications.Post("Map is already up to date");
            return true;
        }

        // write to file
        string path = storage.GetFullPath($"files/{PathUtils.HashToPath(hash)}");
        string dir = Path.GetDirectoryName(path);

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, json);

        // update realm
        realm.RunWrite(r =>
        {
            var existingMap = r.All<RealmMap>().FirstOrDefault(m => m.ID == Map.ID);

            if (existingMap != null)
            {
                var set = existingMap.MapSet;
                if (setStatus) set.SetStatus(-2);
                var prevFile = existingMap.MapSet.GetFileFromHash(existingMap.Hash);

                if (prevFile == null)
                {
                    notifications.PostError("Failed to find previous map file!");
                    return;
                }

                var toRemove = new List<RealmFile>();

                foreach (var file in Map.MapSet.Files)
                {
                    if (set.Files.Any(f => f.Hash == file.Hash))
                        continue;

                    if (file.Name.EndsWith(".fsc"))
                    {
                        if (set.Maps.All(m => m.Hash != file.Hash))
                        {
                            toRemove.Add(file);
                            continue;
                        }
                    }

                    set.Files.Add(file);
                }

                foreach (var file in toRemove)
                {
                    Map.MapSet.Files.Remove(file);

                    var f = set.Files.FirstOrDefault(f => f.Hash == file.Hash);
                    if (f != null) set.Files.Remove(f);
                }

                prevFile.Hash = hash;
                existingMap.Hash = hash;
                Map.Hash = hash;
                r.Remove(existingMap.Filters);
                existingMap.Filters = MapUtils.GetMapFilters(MapInfo, values.MapEvents);

                r.Remove(existingMap.Metadata);
                existingMap.Difficulty = MapInfo.Metadata.Difficulty;
                existingMap.MapSet.Cover = Map.MapSet.Cover;
                existingMap.Metadata = new RealmMapMetadata
                {
                    Title = MapInfo.Metadata.Title,
                    Artist = MapInfo.Metadata.Artist,
                    Mapper = MapInfo.Metadata.Mapper,
                    Source = MapInfo.Metadata.Source,
                    Tags = MapInfo.Metadata.Tags,
                    Background = MapInfo.BackgroundFile,
                    Audio = MapInfo.AudioFile,
                    PreviewTime = MapInfo.Metadata.PreviewTime
                };

                var effectsFile = set.GetFile(MapInfo.EffectFile);

                if (effectsFile == null)
                {
                    set.Files.Add(new RealmFile
                    {
                        Hash = effectsHash,
                        Name = MapInfo.EffectFile
                    });
                }
                else
                    effectsFile.Hash = effectsHash;

                mapStore.UpdateMapSet(Map.MapSet, set.Detach());
            }
            else
            {
                var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var set = Map.MapSet;
                set.Files.Add(new RealmFile
                {
                    Hash = hash,
                    Name = $"{time}.fsc"
                });

                Map.Hash = hash;
                Map.Filters = MapUtils.GetMapFilters(MapInfo, new MapEvents());
                Map.Difficulty = MapInfo.Metadata.Difficulty;
                Map.Metadata = new RealmMapMetadata
                {
                    Title = MapInfo.Metadata.Title,
                    Artist = MapInfo.Metadata.Artist,
                    Mapper = MapInfo.Metadata.Mapper,
                    Source = MapInfo.Metadata.Source,
                    Tags = MapInfo.Metadata.Tags,
                    Background = MapInfo.BackgroundFile,
                    Audio = MapInfo.AudioFile,
                    PreviewTime = MapInfo.Metadata.PreviewTime
                };

                r.Add(set);
                mapStore.AddMapSet(set.Detach());
            }
        });

        notifications.Post("Saved!");
        return true;
    }

    public bool HasChanges()
    {
        string json = JsonConvert.SerializeObject(MapInfo);
        string hash = MapUtils.GetHash(json);
        return hash != Map.Hash;
    }

    private void export() => sendWipNotification();
    private void tryExit() => this.Exit(); // TODO: unsaved changes check
    private void sendWipNotification() => notifications.Post("This is still in development\nCome back later!");

    public void SetKeyMode(int keyMode)
    {
        if (keyMode < Map.KeyCount)
        {
            // check if can be changed
        }

        Map.KeyCount = keyMode;
        MapInfo.KeyCount = keyMode;
        changeHandler.OnKeyModeChanged.Invoke(keyMode);
    }

    public void SetAudio(FileInfo file)
    {
        if (file == null)
            return;

        copyFile(file);
        MapInfo.AudioFile = file.Name;
        Map.Metadata.Audio = file.Name;
        clock.ChangeSource(loadMapTrack());
        changeHandler.OnTimingPointAdded.Invoke();
    }

    public void SetBackground(FileInfo file)
    {
        if (file == null)
            return;

        copyFile(file);
        MapInfo.BackgroundFile = file.Name;
        Map.Metadata.Background = file.Name;
        backgrounds.AddBackgroundFromMap(Map);
    }

    public void SetCover(FileInfo file)
    {
        if (file == null)
            return;

        copyFile(file);
        Map.MapSet.Cover = file.Name;
        MapInfo.CoverFile = file.Name;
    }

    public void SetVideo(FileInfo file)
    {
        if (file == null)
            return;

        copyFile(file);
        MapInfo.VideoFile = file.Name;
    }

    private void copyFile(FileInfo file)
    {
        Stream s = file.OpenRead();
        string hash = MapUtils.GetHash(s);
        string path = storage.GetFullPath($"files/{PathUtils.HashToPath(hash)}");

        if (!File.Exists(path))
        {
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.Copy(file.FullName, path);
        }

        if (Map.MapSet.Files.Any(f => f.Hash == hash))
            return;

        bool found = false;

        Map.MapSet.Files.ForEach(f =>
        {
            if (f.Name == file.Name)
            {
                found = true;
                f.Hash = hash;
            }
        });

        if (found) return;

        Map.MapSet.Files.Add(new RealmFile
        {
            Hash = hash,
            Name = file.Name
        });
    }

    private void startUpload() => Task.Run(uploadSet);

    private async void uploadSet()
    {
        switch (Map.Status)
        {
            case >= 100:
                notifications.PostError("Map is from another game!");
                return;
        }

        var overlay = new LoadingPanel
        {
            Text = "Uploading mapset...",
            SubText = "Checking for duplicate diffs..."
        };

        Schedule(() => game.Overlay = overlay);

        // check for duplicate diffs
        var diffs = Map.MapSet.Maps.Select(m => m.Difficulty).ToList();
        var duplicate = diffs.GroupBy(x => x).Where(g => g.Count() > 1).Select(y => y.Key).ToList();

        if (duplicate.Count > 0)
        {
            notifications.PostError($"Cannot upload mapset!\nDuplicate difficulty names found: {string.Join(", ", duplicate)}");
            return;
        }

        overlay.SubText = "Saving mapset...";

        Map.MapSet.SetStatus(0);
        if (!save(false)) return;

        overlay.SubText = "Uploading mapset...";

        var notification = new LoadingNotification();
        var path = mapStore.Export(Map.MapSet, notification, false);
        var buffer = await File.ReadAllBytesAsync(path);

        var request = fluxel.CreateAPIRequest(Map.MapSet.OnlineID != -1 ? $"/map/{Map.MapSet.OnlineID}/update" : "/maps/upload", HttpMethod.Post);
        request.AddFile("file", buffer);
        request.UploadProgress += (l1, l2) => overlay.SubText = $"Uploading mapset... {(int)((float)l1 / l2 * 100)}%";
        await request.PerformAsync();

        overlay.SubText = "Reading server response...";

        var json = request.GetResponseString();
        var response = JsonConvert.DeserializeObject<APIResponse<APIMapSet>>(json);

        if (response.Status != 200)
        {
            notifications.PostError(response.Message);
            Schedule(overlay.Hide);
            return;
        }

        overlay.SubText = "Updating mapset...";

        realm.RunWrite(r =>
        {
            var set = r.Find<RealmMapSet>(Map.MapSet.ID);
            set.OnlineID = Map.MapSet.OnlineID = response.Data.Id;
            set.SetStatus(response.Data.Status);
            Map.MapSet.SetStatus(response.Data.Status);

            for (var index = 0; index < set.Maps.Count; index++)
            {
                var onlineMap = response.Data.Maps[index];
                var map = set.Maps.First(m => m.Difficulty == onlineMap.Difficulty);
                var loadedMap = Map.MapSet.Maps.First(m => m.Difficulty == onlineMap.Difficulty);

                map.OnlineID = loadedMap.OnlineID = onlineMap.Id;
            }
        });

        Schedule(overlay.Hide);
    }

    private EditorMapInfo getEditorMapInfo(MapInfo map)
    {
        if (map == null) return null;

        var eMap = EditorMapInfo.FromMapInfo(map.Clone());
        eMap.Map = Map;
        return eMap;
    }
}
