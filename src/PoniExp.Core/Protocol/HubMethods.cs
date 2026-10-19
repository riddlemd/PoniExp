namespace PoniExp.Core.Protocol;

/// <summary>
/// Names that cross the SignalR boundary. A rename on one side only breaks silently at runtime, so both
/// the router and the client use these constants.
/// </summary>
public static class HubMethods
{
    public const string Path = "/relay";

    // client -> router
    public const string Hello = nameof(Hello);
    public const string GetChallenge = nameof(GetChallenge);
    public const string Authenticate = nameof(Authenticate);
    public const string RequestJoin = nameof(RequestJoin);
    public const string Send = nameof(Send);
    /// <summary>"I have stored this message; drop it from the queue." Not the agent's acknowledgement.</summary>
    public const string Confirm = nameof(Confirm);
    public const string GetMachines = nameof(GetMachines);

    // router -> client
    public const string Receive = nameof(Receive);
    public const string PresenceChanged = nameof(PresenceChanged);
    public const string MembershipChanged = nameof(MembershipChanged);
}
