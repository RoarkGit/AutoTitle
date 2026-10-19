# AutoTitle (Dalamud plugin)

A Dalamud plugin for FFXIV that changes your title automatically based on your job, gearset, zone,
or duty.

You set a default title, then add rules like "use my Ultimate title in Ultimate raids" or "use a
crafting title on crafters". Whenever your situation changes, AutoTitle picks the first rule that
matches and switches to its title. It's the same as changing your title in the Character window,
so other players see it on your nameplate.

## Rules

Each rule has a title and one or more conditions. All of a rule's conditions have to match for it
to apply, and rules higher in the list win over rules below them. A condition can hold several
values, and it matches if any of them do.

The conditions you can use are:

- Job, either individual jobs or groups like tanks, healers, melee DPS, or Disciples of the Hand
- Duty type, such as dungeons, trials, raids, Ultimates, deep dungeons, or PvP
- Duty, for a specific dungeon, trial, or raid
- Zone and map
- Gearset

Next to each rule is an icon showing whether it's the one in use right now, whether it matches but
a rule above it wins, or whether its title isn't unlocked on your character.

## Alts

By default, all your characters share the same rules. You can also give a character its own rules
and default title from the "Settings for" dropdown. That character's rules are checked first, then
the shared ones, unless you turn off "Fall back to shared rules". Gearset numbers differ between
characters, so gearset rules usually make more sense per character.

## Notes

AutoTitle only changes your title when your rules point to a different one than before, or when
you log in. If you change your title by hand, it stays that way until you switch jobs, zones, and
so on. It also waits until you've finished zoning or watching a cutscene, and it skips any title
your character hasn't unlocked.

## Installing

`/xlsettings` -> Experimental -> Custom Plugin Repositories -> add
`https://raw.githubusercontent.com/RoarkGit/DalamudPlugins/main/pluginmaster.json`, then install
AutoTitle from the plugin installer.

## Commands

- `/autotitle` opens the settings window
- `/autotitle on`, `off`, or `toggle` turns automatic titles on or off
- `/autotitle apply` re-applies the title for your current situation

## Building

```
dotnet build -c Release
```

Then add `bin/Release/AutoTitle.dll` under Dev Plugin Locations in `/xlsettings` > Experimental.
