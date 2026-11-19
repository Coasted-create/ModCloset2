using Dalamud.Bindings.ImGui;
using ModCloset2.Looks;

namespace ModCloset2.Game;

/// <summary>
/// Glamourer's designs for the looks: made through Glamourer's IPC (AddDesign, and DeleteDesign for the one it replaces);
/// when this Glamourer has no such IPC, the design text goes on the clipboard for its Import button. Framework thread.
/// </summary>
public sealed class LooksDesigns(Ipc ipc, Action<string>? log = null) : ILooksDesigns
{
    /// <summary> Glamourer answered once that it has no AddDesign (an older version): the clipboard it is. </summary>
    private bool _noIpc;

    public string Problem
        => !ipc.GlamourerAvailable ? Loc.T("Glamourer is not running.")
            : _noIpc ? Loc.T("This Glamourer cannot take designs from other plugins; update it, or import the design by hand.")
            : string.Empty;

    public Guid? Put(string name, string design, Guid? old)
    {
        if (ipc.AddDesign(design, name) is not { } answer)
        {
            _noIpc = true;
            log?.Invoke("Mod Closet 2 looks: Glamourer has no AddDesign; designs go through the clipboard");
            return null;
        }

        if (answer.Code != Ipc.GlamourerSuccess || answer.Id == Guid.Empty)
        {
            log?.Invoke($"Mod Closet 2 looks: Glamourer did not take a design (code {answer.Code})");
            return null;
        }

        // the design made before for this look is replaced (Glamourer has no IPC to change a design in place)
        if (old is { } before && before != answer.Id)
            ipc.DeleteDesign(before);
        return answer.Id;
    }

    public bool Copy(string text)
    {
        try
        {
            ImGui.SetClipboardText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
