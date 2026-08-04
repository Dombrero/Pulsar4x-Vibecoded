using Pulsar4X.People;

namespace GameEngine.People;

public class AdminSpaceAbilityState// : ComponentAbilityState
{
    public string? ComponentName { get; internal set; }
    public int CommanderID { get; internal set; } = -1;
    internal CommanderDB? Commander { get; set; }
    public AdminLevel SeatType { get; internal set; }
    public bool TryGetCommander([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CommanderDB? commander)
    {
        commander = Commander;
        return commander is not null;
    }


    public AdminSpaceAbilityState(AdminLevel type, string componentName)
    {
        SeatType = type;
        ComponentName = componentName;
    }
}
