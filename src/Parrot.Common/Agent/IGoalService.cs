namespace Parrot.Agent;

/// <summary>Updates the owning agent's persistent goal reminder.</summary>
internal interface IGoalService
{
    Task SetGoal(string goal, CancellationToken cancellationToken);

    void ClearGoal();
}
