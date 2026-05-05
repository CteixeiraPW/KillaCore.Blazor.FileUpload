using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace KillaCore.Blazor.FileUpload.Filters;

/// <summary>
/// When applied, ensures the "userId" query parameter matches the authenticated user's identity.
/// Skips validation if the user is not authenticated (anonymous scenario).
/// </summary>
internal sealed class EnforceAuthenticatedUserFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var identity = context.HttpContext.User.Identity;

        // Only enforce when the user IS authenticated
        if (identity is not { IsAuthenticated: true })
            return;

        // Check query string, route values, or form for "userId"
        string? requestedUserId = context.HttpContext.Request.Query["userId"].FirstOrDefault();

        if(requestedUserId is null && context.ActionArguments.TryGetValue("userId", out var userIdArg))
        {
            requestedUserId = userIdArg as string;
        }

        if (requestedUserId is null)
            return;

        if (!string.Equals(requestedUserId, identity.Name, StringComparison.Ordinal))
        {
            context.Result = new ForbidResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

/// <summary>
/// Resolves and executes EnforceAuthenticatedUserFilter only if it was registered.
/// Safe to leave on the controller even when userIdEnforcement is false.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnforceAuthenticatedUserAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var filter = serviceProvider.GetService<EnforceAuthenticatedUserFilter>();
        return filter == null ? new NoOpFilter() : filter;
    }

    private sealed class NoOpFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context) { }
        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}