using System.Net;

namespace Ansight.Infrastructure.Web;

public sealed class WebClientResult<T>
{
    private WebClientResult(bool isSuccess, T? data, string message, HttpStatusCode? statusCode)
    {
        IsSuccess = isSuccess;
        Data = data;
        Message = message;
        StatusCode = statusCode;
    }

    public bool IsSuccess { get; }

    public T? Data { get; }

    public string Message { get; }

    public HttpStatusCode? StatusCode { get; }

    public static WebClientResult<T> Success(T? data, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new WebClientResult<T>(true, data, string.Empty, statusCode);
    }

    public static WebClientResult<T> Failure(string message, HttpStatusCode? statusCode = null)
    {
        return new WebClientResult<T>(false, default, message, statusCode);
    }
}
