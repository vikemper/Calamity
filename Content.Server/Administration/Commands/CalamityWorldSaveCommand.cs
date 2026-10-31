using Content.Server._Calamity.Persistence;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Server)]
public sealed partial class CalamityWorldSaveCommand : LocalizedEntityCommands
{
    [Dependency] private CalamityWorldPersistenceSystem _persistence = default!;

    public override string Command => "calamityworldsave";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        shell.WriteLine(Loc.GetString(
            _persistence.RequestManualSave()
                ? "cmd-calamityworldsave-queued"
                : "cmd-calamityworldsave-unavailable"));
    }
}
