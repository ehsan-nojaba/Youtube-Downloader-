namespace YtDownloader.Application;

public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, string[]>? Details = null);

public interface IResult<TSelf> where TSelf : IResult<TSelf>
{
    static abstract TSelf Failure(Error error);
}

public sealed class Result<T> : IResult<Result<T>>
{
    private Result(T? value, Error? error) => (Value, Error) = (value, error);
    public bool IsSuccess => Error is null;
    public T? Value { get; }
    public Error? Error { get; }
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(Error error) => new(default, error);
}
