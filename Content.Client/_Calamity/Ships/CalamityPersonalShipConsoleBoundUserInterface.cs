using Content.Client._Calamity.Ships.UI;
using Content.Shared._Calamity.Ships;
using Robust.Client.UserInterface;

namespace Content.Client._Calamity.Ships;

public sealed class CalamityPersonalShipConsoleBoundUserInterface : BoundUserInterface
{
    private CalamityPersonalShipConsoleWindow? _window;

    public CalamityPersonalShipConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<CalamityPersonalShipConsoleWindow>();
        _window.ActionRequested += OnActionRequested;
        SendMessage(new CalamityPersonalShipConsoleActionMessage(CalamityPersonalShipConsoleAction.Refresh));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is CalamityPersonalShipConsoleState shipState)
            _window?.UpdateState(shipState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }

    private void OnActionRequested(CalamityPersonalShipConsoleAction action, NetEntity? ship = null)
    {
        SendMessage(new CalamityPersonalShipConsoleActionMessage(action, ship));
    }
}
