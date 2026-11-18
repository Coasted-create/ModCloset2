using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ModCloset2.OnYou.Game;

/// <summary>
/// Whether one of the game's own text fields is typed in (the chat line, a search box). The same call as the window's
/// <c>ClosetWindow.GameTyping</c>: while it is true, On you reads no keys and hides none from the game. If the call
/// ever fails, it is taken as "no" from then on and said once in the log.
/// </summary>
public sealed unsafe class GameTyping
{
    private readonly IPluginLog _log;
    private bool _broken;

    public GameTyping(IPluginLog log)
        => _log = log;

    public bool Now()
    {
        if (_broken)
            return false;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
                return false;
            var atk = ui->GetRaptureAtkModule();
            return atk != null && atk->IsTextInputActive();
        }
        catch (Exception e)
        {
            _broken = true;
            _log.Warning(e, "Mod Closet 2: On you cannot tell whether the game's chat is typed in; assuming it is not");
            return false;
        }
    }
}
