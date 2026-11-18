# Mod Closet 2

Your [Penumbra](https://github.com/xivdev/Penumbra) mods as a closet. Mod Closet 2 is a Dalamud plugin for
FINAL FANTASY XIV that sorts your mods onto shelves by what they are on you (tops, shoes, earrings, tails, nails…),
lets you try one on with a click, and keeps it only when you say so.

> **Status: test build (0.7.10).** It has been used on one large mod library. Expect rough edges, and please report them.

## What it does

- **Shelves by piece.** The closet looks inside each mod and works out what it is on the body, also when a jacket is
  worn through the ring slot. A mod it is unsure about waits under "To sort"; drag a card to another shelf and the
  closet remembers.
- **Try on, Keep, Put back.** A click tries a mod on over your look. Keep makes it stay, Put back ends the try, Undo
  takes the last step back. Nothing is kept until you say so.
- **Moving is locked.** The lock beside Undo stops a mod from being dragged onto a wrong shelf by accident; click it
  to drag mods freely. Move to… in a card's menu always works.
- **Options and colours beside the closet.** Click a mod and a panel opens next to the closet with its colours and
  every option laid out, so you can pick the right face, tail or size before you keep it. Drag it away to place it
  anywhere.
- **Bodies, skin and makeup apart.** Body mods, skin textures, face sculpts and makeup each have their own shelf; a
  skin goes over the body you wear and makeup over the sculpt.
- **Which slot a mod uses.** Every card names the game slot it is worn through, marked when it is unusual (a jacket
  in the wrist slot), and a slot filter shows only the mods of one slot
  (the right and the left ring slot apart).
- **Body and race filters.** Show only the mods made for your body (Rue, YAB, Bibo+, TBSE…) or for your race.
- **Options at a glance.** A card whose mod has options to set (sleeves, length, a face number…) says so, and
  shows which ones when you point at it.
- **Rings for either hand.** Rings sit on a right and a left shelf. A ring made for one hand can be copied into a mod
  of your own that is worn through the other ring slot, to free up a slot.
- **A photo on every card.** The closet draws a picture of each piece itself, in the background: clothes, jewellery,
  hair, tails, makeup and skin, on a dummy made from the game's own head and body. Nothing is put on your character.
- **Outfit colours.** Recolour what you wear: one area, a whole piece in one step, or the whole outfit.
- **Looks.** What you wear together is saved as a look by itself, or with Save look under a name of your own;
  you choose whether bodies, hair, makeup and the like go with it.
- **On you.** Point at a part of your character to flip through that shelf.
- **Make it its own mod.** Copy one piece out of a big mod into a new mod of its own. The mod it comes from is not
  changed.
- **Only things to wear.** Animations, effects, sounds and other files that are nothing to wear are hidden; a switch in
  Settings shows them on Everything else.
- **Three shapes.** A wide window, a slim one, and On you. English and German.

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) (through XIVLauncher), API level 15
- [Penumbra](https://github.com/xivdev/Penumbra) and [Glamourer](https://github.com/Ottermandias/Glamourer),
  installed and enabled

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/Coasted-create/ModCloset2/main/repo.json`, tick it, and save.
3. Open `/xlplugins`, search for **Mod Closet 2**, and install it.

## Use

`/closet2` opens the closet. The first start reads all your mods in the background; with a large library that takes
about half a minute.

- Keep turns mods on or off in your Penumbra collection, as if you had done it there by hand.
- Photos come by themselves: the shelf you look at first, the rest while the closet is open. They are kept in the
  plugin's own folder. Settings → "Photos of your pieces" switches them off.
- `/closet2 rescan` reads your mods again. `/closet2 debug` opens a plain fallback window.

## Credits and licence

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). Mod Closet 2 is not affiliated with Penumbra, Glamourer,
Dalamud or SQUARE ENIX. FINAL FANTASY XIV © SQUARE ENIX CO., LTD.
