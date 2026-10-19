namespace PoniExp.Router;

/// <summary>A refusal the caller can act on; its message goes back to the client or admin as is.</summary>
public sealed class RouterException(string message, RouterErrorKind kind = RouterErrorKind.Invalid) : Exception(message)
{
    public RouterErrorKind Kind { get; } = kind;
}

public enum RouterErrorKind
{
    Invalid,
    NotFound,
    Conflict,
}
