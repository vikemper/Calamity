using System.Globalization;
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
        if (args.Length == 0 || args[0].Equals("save", System.StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
                return;
            }

            shell.WriteLine(Loc.GetString(
                _persistence.RequestManualSave()
                    ? "cmd-calamityworldsave-queued"
                    : "cmd-calamityworldsave-unavailable"));
            return;
        }

        if (args[0].Equals("list", System.StringComparison.OrdinalIgnoreCase) && args.Length == 1)
        {
            var snapshots = _persistence.GetAvailableSnapshots();
            if (snapshots.Count == 0)
            {
                shell.WriteLine(Loc.GetString("cmd-calamityworldsave-list-empty"));
                return;
            }

            shell.WriteLine(Loc.GetString("cmd-calamityworldsave-list-header"));
            foreach (var epoch in snapshots)
            {
                shell.WriteLine(epoch.ToString(CultureInfo.InvariantCulture));
            }

            return;
        }

        if (args[0].Equals("restore", System.StringComparison.OrdinalIgnoreCase) && args.Length == 2)
        {
            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            {
                shell.WriteError(Loc.GetString("cmd-parse-failure-integer", ("arg", args[1])));
                return;
            }

            shell.WriteLine(Loc.GetString(
                _persistence.RequestRestore(epoch)
                    ? "cmd-calamityworldsave-restore-queued"
                    : "cmd-calamityworldsave-restore-unavailable"));
            return;
        }

        if (args[0].Equals("cancel-restore", System.StringComparison.OrdinalIgnoreCase) && args.Length == 1)
        {
            shell.WriteLine(Loc.GetString(
                _persistence.CancelRestoreRequest()
                    ? "cmd-calamityworldsave-restore-cancelled"
                    : "cmd-calamityworldsave-restore-not-pending"));
            return;
        }

        shell.WriteError(Loc.GetString("cmd-calamityworldsave-invalid-arguments"));
    }
}
