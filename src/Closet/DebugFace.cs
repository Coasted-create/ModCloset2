using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ModCloset2.Core;

namespace ModCloset2.Closet;

/// <summary>
/// A plain, temporary window (ordinary ImGui, no styling) so the engine can be tried in the game on its own: scan status,
/// the rail with counts and what is on, the cards of the picked shelf as lines, the try-on line, Undo, search, To sort and
/// Move. The real window comes from the window area and replaces it. Draws only from the session.
/// </summary>
public sealed class DebugFace : Window
{
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Gold  = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.5f, 1f);
    private static readonly Vector4 Rose  = new(0.95f, 0.55f, 0.7f, 1f);

    private readonly IClosetSession _session;
    private readonly Action<Exception> _logError;
    private string _search = string.Empty;
    private string _newShelf = string.Empty;
    private PartKey? _why;
    private bool _failedOnce;

    public DebugFace(IClosetSession session, Action<Exception> logError)
        : base(Loc.T("Mod Closet 2") + "###ModCloset2Plain")
    {
        _session        = session;
        _logError       = logError;
        Size            = new Vector2(1100, 760);
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 420), MaximumSize = new Vector2(4000, 3000) };
    }

    /// <summary> Closing the window while trying puts everything back (SPEC). </summary>
    public override void OnClose()
        => _session.PutBack();

    public override void Draw()
    {
        try
        {
            DrawAll();
        }
        catch (Exception ex)
        {
            // Draw never throws: one calm line, and the details once in the log
            ImGui.TextColored(Gold, Loc.T("Something went wrong drawing the closet."));
            if (!_failedOnce)
            {
                _failedOnce = true;
                _logError(ex);
            }
        }
    }

    private void DrawAll()
    {
        var scan = _session.Scan;
        if (scan.Problem.Length > 0)
        {
            ImGui.TextColored(Gold, scan.Problem);
            return;
        }

        if (!scan.Ready)
        {
            ImGui.TextColored(Muted, scan.Text);
            return;
        }

        DrawTopBar();
        if (scan.Text.Length > 0)
            ImGui.TextColored(Muted, scan.Text);
        if (_session.Status is { Age: < 8 } status)
            ImGui.TextColored(status.IsError ? Gold : Muted, status.Text);
        DrawSuggestion();
        DrawTryBar();
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##rail", new Vector2(Math.Min(300, avail.X * 0.3f), 0), true))
            DrawRail();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##right", new Vector2(0, 0), true))
            DrawRight();
        ImGui.EndChild();
    }

    private void DrawTopBar()
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("##search", Loc.T("Search all shelves"), ref _search, 128))
            _session.Search = _search;
        ImGui.SameLine();
        ImGui.BeginDisabled(!_session.CanUndo);
        if (ImGui.Button(Loc.T("Undo")))
            _session.Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Read my mods again")))
            _session.Rescan();
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Write the census file")))
            _session.WriteCensus();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        var language = _session.Language switch { "en" => "English", "de" => "Deutsch", _ => Loc.T("As the game") };
        if (ImGui.BeginCombo("##language", language))
        {
            if (ImGui.Selectable(Loc.T("As the game"), _session.Language.Length == 0))
                _session.Language = string.Empty;
            foreach (var (code, name) in Loc.Languages)
                if (ImGui.Selectable(name, _session.Language == code))
                    _session.Language = code;
            ImGui.EndCombo();
        }
    }

    private void DrawSuggestion()
    {
        if (_session.Suggestion is not { } s)
            return;
        ImGui.TextColored(Rose, s.Text);
        ImGui.SameLine();
        ImGui.TextUnformatted(s.Detail);
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Move them too")))
            _session.AcceptSuggestion();
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Leave them")))
            _session.DismissSuggestion();
    }

    private void DrawTryBar()
    {
        var t = _session.Trying;
        if (!t.Active)
        {
            ImGui.TextColored(Muted, t.Busy ? Loc.T("One moment…") : Loc.T("Click a card to try it on."));
            return;
        }

        ImGui.TextColored(Rose, $"{t.Lead} {t.Name}");
        if (t.Note.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "· " + t.Note);
        }

        if (t.Busy)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "…");
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Put back")))
            _session.PutBack();
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Keep")))
            _session.Keep();

        if (t.Extras.Count > 0)
        {
            ImGui.TextColored(Muted, Loc.T("Also in this mod:"));
            foreach (var extra in t.Extras)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"+ {extra.Name}##extra{extra.Key}"))
                    _session.Try(extra.Key);
            }
        }
    }

    private void DrawRail()
    {
        var group = (PieceGroup)(-1);
        foreach (var row in _session.Rail)
        {
            if (row.Group != group)
            {
                group = row.Group;
                var label = group switch
                {
                    PieceGroup.Clothes   => Loc.T("Clothes"),
                    PieceGroup.Jewellery => Loc.T("Jewellery"),
                    PieceGroup.Body      => Loc.T("Body"),
                    PieceGroup.Mine      => Loc.T("Mine"),
                    _                    => string.Empty,
                };
                if (label.Length > 0)
                    ImGui.TextColored(Muted, label);
            }

            var text = row.Badge > 0 ? $"{row.Name} ({row.Badge})" : $"{row.Name}  {row.Count}";
            if (ImGui.Selectable($"{text}##rail{row.Id}", row.Id == _session.Selected))
                _session.Select(row.Id);
            if (row.WornName.Length > 0)
                ImGui.TextColored(Green, "   " + row.WornName);
        }
    }

    private void DrawRight()
    {
        var searching = _session.Search.Trim().Length > 0;
        var cards     = _session.Cards;
        var name      = searching ? Loc.F("Search · {0}", cards.Count) : _session.Rail.FirstOrDefault(r => r.Id == _session.Selected)?.Name ?? string.Empty;
        ImGui.TextUnformatted(name);

        var kinds = _session.Kinds;
        foreach (var chip in kinds)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{(chip.Id == _session.Kind ? "• " : string.Empty)}{chip.Name} {chip.Count}##kind{chip.Id}"))
                _session.Kind = chip.Id;
        }

        DrawWearing();
        ImGui.Separator();

        if (!searching && _session.Selected == Pieces.ToSort)
        {
            DrawToSort();
            return;
        }

        if (!searching && _session.Selected is not (Pieces.Outfits or Pieces.ToSort or Pieces.Other or Pieces.Hidden))
        {
            if (ImGui.SmallButton(Loc.T("Nothing")))
                _session.TryNothing(_session.Selected);
            ImGui.SameLine();
            ImGui.TextColored(Muted, Loc.T("take off what is on here"));
        }

        if (cards.Count == 0)
        {
            ImGui.TextColored(Muted, searching ? Loc.F("Nothing found for “{0}”.", _session.Search.Trim()) : Loc.T("Nothing on this shelf."));
            return;
        }

        if (!ImGui.BeginChild("##cards", new Vector2(0, 0), false))
        {
            ImGui.EndChild();
            return;
        }

        // only the rows in view are drawn: a shelf can hold thousands of cards
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var first     = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight) - 1);
        var visible   = (int)(ImGui.GetWindowHeight() / rowHeight) + 3;
        var last      = Math.Min(cards.Count, first + visible);
        if (first > 0)
            ImGui.Dummy(new Vector2(1, first * rowHeight));
        for (var i = first; i < last; ++i)
            DrawCard(cards[i], searching);
        if (last < cards.Count)
            ImGui.Dummy(new Vector2(1, (cards.Count - last) * rowHeight));
        ImGui.EndChild();
    }

    private void DrawCard(Card card, bool searching)
    {
        ImGui.PushID(card.Key.ToString());
        if (card.State == CardState.Trying)
        {
            if (ImGui.SmallButton(Loc.T("Take out")))
                _session.StopTrying(card.Key);
        }
        else if (card.State == CardState.On)
        {
            ImGui.TextColored(Green, Loc.T("On"));
        }
        else if (ImGui.SmallButton(Loc.T("Try")))
        {
            _session.Try(card.Key);
        }

        ImGui.SameLine();
        var marks = new List<string>();
        if (card.IsNew)
            marks.Add(Loc.T("New"));
        if (card.Versions > 1)
            marks.Add(Loc.F("{0} versions", card.Versions));
        if (card.PartsInMod > 1)
            marks.Add(Loc.F("{0}-piece set", card.PartsInMod));
        if (searching || card.KindName.Length > 0)
            marks.Add(searching ? card.PieceName : card.KindName);
        ImGui.TextColored(card.State == CardState.Trying ? Rose : new Vector4(1, 1, 1, 1), card.Name);
        if (marks.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, string.Join(" · ", marks));
        }

        if (card.Key.Socket != Socket.None)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130);
            if (ImGui.BeginCombo("##move", Loc.T("Move to")))
            {
                foreach (var target in _session.MoveTargets)
                    if (ImGui.Selectable(target.Group == PieceGroup.Mine ? target.Name : Loc.T(target.Name), target.Id == card.Piece))
                        _session.Move(card.Key, target.Id);
                ImGui.SetNextItemWidth(110);
                ImGui.InputTextWithHint("##newshelf", Loc.T("New shelf…"), ref _newShelf, 40);
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("Make")) && _newShelf.Trim().Length > 0)
                {
                    var id = _session.AddPiece(_newShelf);
                    _session.Move(card.Key, id);
                    _newShelf = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (card.Piece == Pieces.Hidden ? ImGui.SmallButton(Loc.T("Show again")) : ImGui.SmallButton(Loc.T("Hide")))
            {
                if (card.Piece == Pieces.Hidden)
                    _session.Unhide(card.Key);
                else
                    _session.Hide(card.Key);
            }

            if (card.Moved)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.T("Put back where the closet had it")))
                    _session.PutBackWhereDetected(card.Key);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Why is it here?")))
                _why = _why == card.Key ? null : card.Key;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Open in Penumbra")))
            _session.OpenInPenumbra(card.Key);

        if (_why == card.Key)
            foreach (var e in _session.WhyHere(card.Key))
                ImGui.TextColored(Muted, $"      {Loc.T(e.Kind.ToString())}: {e.Value}");
        ImGui.PopID();
    }

    private void DrawWearing()
    {
        if (_session.Wearing is not { } w)
        {
            if (_session.Search.Trim().Length == 0 && _session.Selected is not (Pieces.Outfits or Pieces.ToSort or Pieces.Hidden))
                ImGui.TextColored(Muted, Loc.T("Nothing on here right now."));
            return;
        }

        ImGui.TextColored(Green, Loc.F("Wearing {0}", w.Name));
        foreach (var option in w.Options)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(170);
            if (!ImGui.BeginCombo($"##opt{option.Group}", $"{option.Group}: {string.Join(", ", option.On)}"))
                continue;
            foreach (var choice in option.Choices)
            {
                var on = option.On.Contains(choice);
                if (!ImGui.Selectable(choice, on))
                    continue;
                if (!option.Multi)
                    _session.SetOption(w.Key, option.Group, new[] { choice });
                else
                    _session.SetOption(w.Key, option.Group, on ? option.On.Where(c => c != choice).ToList() : option.On.Append(choice).ToList());
            }

            ImGui.EndCombo();
        }

        if (w.Versions > 1)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.F("Version {0} of {1}", w.Version + 1, w.Versions)))
                _session.SetVersion(w.Key, (w.Version + 1) % w.Versions);
        }
    }

    private void DrawToSort()
    {
        var items = _session.ToSort;
        if (items.Count == 0)
        {
            ImGui.TextColored(Muted, Loc.T("Nothing waits to be sorted."));
            return;
        }

        foreach (var item in items.Take(200))
        {
            ImGui.PushID(item.Key.ToString());
            ImGui.TextUnformatted(item.Name);
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"{item.SlotText} · {item.SitsText} · {item.WordsText}");
            foreach (var guess in item.Guesses)
            {
                ImGui.SameLine();
                var label = guess.Id.Length == 0 ? Loc.F("New shelf: {0}", guess.Name) : guess.Group == PieceGroup.Mine ? guess.Name : Loc.T(guess.Name);
                if (ImGui.SmallButton(label))
                    _session.Move(item.Key, guess.Id.Length == 0 ? _session.AddPiece(guess.Name) : guess.Id);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            if (ImGui.BeginCombo("##other", Loc.T("Other")))
            {
                foreach (var target in _session.MoveTargets)
                    if (ImGui.Selectable(target.Group == PieceGroup.Mine ? target.Name : Loc.T(target.Name)))
                        _session.Move(item.Key, target.Id);
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Try")))
                _session.Try(item.Key);
            ImGui.PopID();
        }

        if (items.Count > 200)
            ImGui.TextColored(Muted, Loc.F("…and {0} more.", items.Count - 200));
    }
}
