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
    public const string GetGroups = nameof(GetGroups);

    // rooms: the router keeps each room's signed membership log
    public const string CreateRoom = nameof(CreateRoom);
    public const string SubmitRoomEvent = nameof(SubmitRoomEvent);
    public const string GetRoom = nameof(GetRoom);
    public const string GetMyRooms = nameof(GetMyRooms);

    // the job board: open to every approved machine
    public const string SubmitJobAction = nameof(SubmitJobAction);
    public const string GetJobs = nameof(GetJobs);
    public const string GetJob = nameof(GetJob);
    public const string RenewJobLeases = nameof(RenewJobLeases);

    /// <summary>A daemon reports what its machine has (after each authentication).</summary>
    public const string ReportCapabilities = nameof(ReportCapabilities);
    /// <summary>A machine's new encryption key (an <see cref="EncryptionKeyUpdate"/>).</summary>
    public const string RotateEncryptionKey = nameof(RotateEncryptionKey);

    // the owner and the approval desk
    public const string GetOwner = nameof(GetOwner);
    public const string GetHere = nameof(GetHere);
    public const string SetHere = nameof(SetHere);
    /// <summary>The desk machine ends its own desk (no owner signature needed).</summary>
    public const string LeaveDesk = nameof(LeaveDesk);
    /// <summary>A sealed, owner-signed admin command; returns a <see cref="SignedAdminResult"/>.</summary>
    public const string RunAdmin = nameof(RunAdmin);
    /// <summary>The owner-signed key handovers, oldest first.</summary>
    public const string GetOwnerHandovers = nameof(GetOwnerHandovers);
    /// <summary>The desk turns down a docker exec change.</summary>
    public const string DenyAdminRequest = nameof(DenyAdminRequest);
    /// <summary>The owner-signed revocations (each a signed <c>revoke &lt;machine id&gt;</c>).</summary>
    public const string GetRevocations = nameof(GetRevocations);
    /// <summary>A joining machine whose SSH agent holds the owner key approves itself: a signed <c>approve &lt;its id&gt;</c>.</summary>
    public const string ApproveOwnJoin = nameof(ApproveOwnJoin);

    // files (the chunks themselves go over HTTP, under BlobsPath, with a ticket)
    public const string BlobsPath = "/blobs";
    public const string BeginUpload = nameof(BeginUpload);
    public const string ResumeUpload = nameof(ResumeUpload);
    public const string CompleteUpload = nameof(CompleteUpload);
    public const string RequestDownload = nameof(RequestDownload);
    /// <summary>"This machine has the blob (verified), or gave up on it": the router may delete it once all have.</summary>
    public const string ReleaseBlob = nameof(ReleaseBlob);
    public const string CancelBlob = nameof(CancelBlob);
    public const string GetBlobs = nameof(GetBlobs);

    // router -> client
    public const string Receive = nameof(Receive);
    public const string PresenceChanged = nameof(PresenceChanged);
    public const string MembershipChanged = nameof(MembershipChanged);
    /// <summary>A room this machine is involved in changed (a <see cref="RoomState"/>), or collapsed.</summary>
    public const string RoomChanged = nameof(RoomChanged);
    /// <summary>A job changed (a <see cref="JobChange"/>), to every approved machine.</summary>
    public const string JobChanged = nameof(JobChanged);
    /// <summary>The desk changed (a <see cref="HereClaim"/>, or null when it ended), to every approved machine.</summary>
    public const string HereChanged = nameof(HereChanged);
    /// <summary>The owner key was handed over (the signed handovers, oldest first), to every approved machine.</summary>
    public const string OwnerChanged = nameof(OwnerChanged);
    /// <summary>The machines waiting to join, to the desk machine only.</summary>
    public const string PendingJoinsChanged = nameof(PendingJoinsChanged);
    /// <summary>The docker exec changes waiting for a signature, to the desk machine only.</summary>
    public const string AdminRequestsChanged = nameof(AdminRequestsChanged);
    /// <summary>A machine was revoked: the signed revocations, to every approved machine.</summary>
    public const string RevocationsChanged = nameof(RevocationsChanged);
    /// <summary>A machine rotated its encryption key (its <see cref="MachineInfo"/>), to every approved machine.</summary>
    public const string MachineKeyChanged = nameof(MachineKeyChanged);
}
