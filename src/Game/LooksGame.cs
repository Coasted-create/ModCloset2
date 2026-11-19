using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using ModCloset2.Looks;

namespace ModCloset2.Game;

/// <summary>
/// The game as a look worn by itself needs it: the character's gearsets (RaptureGearsetModule), the jobs (ClassJob), and
/// which of them is on now. Framework thread only. The current gearset and job are read every frame (two reads).
/// </summary>
public sealed unsafe class LooksGame(IObjectTable objects, IDataManager data) : ILooksGame
{
    private IReadOnlyList<(string Abbreviation, string Name)>? _jobs;

    public IReadOnlyList<(int Number, string Name, string Job)> Gearsets
    {
        get
        {
            var list   = new List<(int, string, string)>();
            var module = RaptureGearsetModule.Instance();
            if (module is null)
                return list;
            var jobs = data.GetExcelSheet<ClassJob>();
            for (var i = 0; i < 100; ++i)
            {
                if (!module->IsValidGearset(i))
                    continue;
                var entry = module->GetGearset(i);
                if (entry is null)
                    continue;
                var job = jobs.TryGetRow(entry->ClassJob, out var row) ? row.Abbreviation.ExtractText() : string.Empty;
                list.Add((i + 1, entry->NameString, job));
            }

            return list;
        }
    }

    public IReadOnlyList<(string Abbreviation, string Name)> Jobs
        => _jobs ??= data.GetExcelSheet<ClassJob>()
            .Where(j => j.RowId > 0 && j.JobIndex > 0 && j.Abbreviation.ExtractText().Length > 0)
            .OrderBy(j => j.UIPriority)
            .Select(j => (j.Abbreviation.ExtractText(), Capital(j.Name.ExtractText())))
            .ToList();

    public int CurrentGearset
    {
        get
        {
            var module = RaptureGearsetModule.Instance();
            return module is null || objects.LocalPlayer is null ? 0 : module->CurrentGearsetIndex + 1;
        }
    }

    public string CurrentJob
        => objects.LocalPlayer?.ClassJob.ValueNullable is { } job ? job.Abbreviation.ExtractText() : string.Empty;

    private static string Capital(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
