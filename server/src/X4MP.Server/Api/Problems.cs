using Microsoft.AspNetCore.Diagnostics;

namespace X4MP.Server.Api;

/// <summary>Builds the RFC 7807 error responses of the admin API (server-design 4.1). Every error leaves the server through here.</summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static ApiProblem Make(
        int status, string code, string title, string? detail = null, Dictionary<string, string[]>? errors = null,
        Dictionary<string, string>? errorCodes = null, long? receivedBytes = null) =>
        new("urn:x4mp:problem:" + code, title, status, code, detail, errors, errorCodes, receivedBytes);

    public static IResult Result(
        int status, string code, string title, string? detail = null, Dictionary<string, string[]>? errors = null,
        Dictionary<string, string>? errorCodes = null, long? receivedBytes = null) =>
        Results.Json(Make(status, code, title, detail, errors, errorCodes, receivedBytes), ApiJsonContext.Default.ApiProblem, ContentType, status);

    /// <summary>400 with one message list per invalid field (<c>errors[field]</c>).</summary>
    public static IResult Validation(Dictionary<string, string[]> errors, string title = "One or more fields are invalid.") =>
        Result(StatusCodes.Status400BadRequest, "ValidationFailed", title, null, errors);

    /// <summary>400 for a single invalid field.</summary>
    public static IResult Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static IResult NotFound(string what) =>
        Result(StatusCodes.Status404NotFound, "NotFound", "Not found.", $"{what} does not exist.");

    public static IResult Conflict(string code, string detail) =>
        Result(StatusCodes.Status409Conflict, code, "Conflict.", detail);

    public static IResult NotImplemented(string detail) =>
        Result(StatusCodes.Status501NotImplemented, "NotImplemented", "Not implemented.", detail);

    /// <summary>The generic code and title for a bare status code (a response the framework or the authorization layer ended without a body).</summary>
    public static (string Code, string Title) ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ("InvalidRequest", "The request could not be understood."),
        StatusCodes.Status401Unauthorized => ("Unauthorized", "Authentication is required."),
        StatusCodes.Status403Forbidden => ("Forbidden", "You are not allowed to do that."),
        StatusCodes.Status404NotFound => ("NotFound", "Not found."),
        StatusCodes.Status405MethodNotAllowed => ("MethodNotAllowed", "Method not allowed."),
        StatusCodes.Status413PayloadTooLarge => ("TooLarge", "The request body is too large."),
        StatusCodes.Status415UnsupportedMediaType => ("UnsupportedMediaType", "Send the body as application/json."),
        StatusCodes.Status429TooManyRequests => ("RateLimited", "Too many requests."),
        >= 500 => ("InternalError", "The server failed to process the request."),
        _ => ("Error", "The request failed."),
    };

    private static bool IsApiPath(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the problem+json safety nets: an exception handler (a 5xx or the status of a <see cref="BadHttpRequestException"/>, never the
    /// exception text) and status code pages for the empty 400/401/403/404/405/415 responses the framework and the authorization layer
    /// produce under <c>/api</c>. Call before authentication so every layer below is covered.
    /// </summary>
    public static WebApplication UseApiProblems(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            int status = error is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            var (code, title) = ForStatus(status);
            context.Response.StatusCode = status;
            context.Response.ContentType = ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await System.Text.Json.JsonSerializer.SerializeAsync(
                context.Response.Body, Make(status, code, title), ApiJsonContext.Default.ApiProblem, context.RequestAborted).ConfigureAwait(false);
        }));

        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            if (!IsApiPath(http.Request.Path))
            {
                return;
            }

            int status = http.Response.StatusCode;
            var (code, title) = ForStatus(status);
            http.Response.ContentType = ContentType;
            await System.Text.Json.JsonSerializer.SerializeAsync(
                http.Response.Body, Make(status, code, title), ApiJsonContext.Default.ApiProblem, http.RequestAborted).ConfigureAwait(false);
        });
        return app;
    }
}
