using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PCEssentials.Core;

namespace PCEssentials.Services;

public sealed class ScratchPage
{
    public string Title { get; set; } = "Note";
    public string Text { get; set; } = "";
}

/// Autosaved notes in tabs, kept at %APPDATA%\PCEssentials\scratchpad.json.
public sealed class Scratchpad
{
    private readonly string _file = Path.Combine(Paths.DataDir, "scratchpad.json");
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public List<ScratchPage> Pages { get; } = new();
    public int Selected { get; set; }
    public ScratchPage Current => Pages[Math.Clamp(Selected, 0, Pages.Count - 1)];

    public Scratchpad()
    {
        try
        {
            if (File.Exists(_file)) Pages.AddRange(JsonSerializer.Deserialize<List<ScratchPage>>(File.ReadAllText(_file)) ?? []);
        }
        catch (Exception ex) { Log.Error("scratchpad load", ex); }
        if (Pages.Count == 0) Pages.Add(new ScratchPage { Title = "Note 1" });
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    public void Edited(string text)
    {
        Current.Text = text;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Add()
    {
        Pages.Add(new ScratchPage { Title = $"Note {Pages.Count + 1}" });
        Selected = Pages.Count - 1;
        SaveNow();
    }

    public void RemoveCurrent()
    {
        if (Pages.Count <= 1) { Current.Text = ""; SaveNow(); return; }
        Pages.RemoveAt(Selected);
        Selected = Math.Max(0, Selected - 1);
        SaveNow();
    }

    public void SaveNow()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(Pages)); }
        catch (Exception ex) { Log.Error("scratchpad save", ex); }
    }
}
