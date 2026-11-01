using Content.Server.Administration;
using Content.Server.GameTicking.Presets;
using Content.Shared.Administration;
using Content.Shared._Calamity.CCVar;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Console;

namespace Content.Server.GameTicking.Commands
{
    [AdminCommand(AdminFlags.Round)]
    public sealed partial class GoLobbyCommand : IConsoleCommand
    {
        [Dependency] private IEntityManager _e = default!;
        [Dependency] private IConfigurationManager _configuration = default!;

        public string Command => "golobby";
        public string Description => "Enables the lobby and restarts the round.";
        public string Help => $"Usage: {Command} / {Command} <preset>";
        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var ticker = _e.System<GameTicker>();
            if (_configuration.GetCVar(CalamityCCVars.PersistentRoundEnabled) &&
                ticker.RunLevel == GameRunLevel.InRound)
            {
                shell.WriteLine(Loc.GetString("calamity-persistent-round-protected"));
                return;
            }

            GamePresetPrototype? preset = null;
            var presetName = string.Join(" ", args);

            if (args.Length > 0)
            {
                if (!ticker.TryFindGamePreset(presetName, out preset))
                {
                    shell.WriteLine($"No preset found with name {presetName}");
                    return;
                }
            }

            var config = IoCManager.Resolve<IConfigurationManager>();
            config.SetCVar(CCVars.GameLobbyEnabled, true);

            ticker.RestartRound();

            if (preset != null)
            {
                ticker.SetGamePreset(preset);
            }

            shell.WriteLine($"Enabling the lobby and restarting the round.{(preset == null ? "" : $"\nPreset set to {presetName}")}");
        }
    }
}
