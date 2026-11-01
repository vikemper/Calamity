using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._Calamity.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Console;

namespace Content.Server.GameTicking.Commands
{
    [AdminCommand(AdminFlags.Round)]
    sealed partial class EndRoundCommand : IConsoleCommand
    {
        [Dependency] private IEntityManager _e = default!;
        [Dependency] private IConfigurationManager _configuration = default!;

        public string Command => "endround";
        public string Description => "Ends the round and moves the server to PostRound.";
        public string Help => String.Empty;

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (_configuration.GetCVar(CalamityCCVars.PersistentRoundEnabled))
            {
                shell.WriteLine(Loc.GetString("calamity-persistent-round-protected"));
                return;
            }

            var ticker = _e.System<GameTicker>();

            if (ticker.RunLevel != GameRunLevel.InRound)
            {
                shell.WriteLine("This can only be executed while the game is in a round.");
                return;
            }

            ticker.EndRound();
        }
    }
}
