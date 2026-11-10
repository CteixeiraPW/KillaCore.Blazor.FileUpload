using KillaCore.Blazor.FileUpload.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace KillaCore.Blazor.FileUpload.Filters;

/// <summary>
/// Opts every action on <see cref="UploadsController"/> out of authorization.
/// <para>
/// The controller carries no <c>[AllowAnonymous]</c> of its own on purpose. That attribute is
/// explicit authorization metadata, so it short-circuits the host application's
/// <c>AuthorizationOptions.FallbackPolicy</c>: an app that required an authenticated user for
/// every endpoint would still have had the file-receiving endpoint wide open, and could not have
/// closed it without replacing the controller.
/// </para>
/// <para>
/// Applied to the whole controller rather than the upload action alone, because the pipeline is
/// all-or-nothing: a caller that cannot obtain a policy or a token cannot upload anyway.
/// </para>
/// </summary>
internal sealed class AllowAnonymousUploadsConvention : IActionModelConvention
{
    public void Apply(ActionModel action)
    {
        if (action.Controller.ControllerType.AsType() != typeof(UploadsController))
            return;

        // This has to reach ENDPOINT METADATA, not just the filter collection. Under endpoint
        // routing the fallback policy is applied by AuthorizationMiddleware, which looks for
        // IAllowAnonymous in the endpoint's metadata and never consults MVC's filter pipeline —
        // so an AllowAnonymousFilter on its own leaves the endpoint behind the very policy it
        // was meant to opt out of, while looking like it had worked.
        foreach (var selector in action.Selectors)
            selector.EndpointMetadata.Add(new AllowAnonymousAttribute());

        // Kept as well, for an [Authorize] enforced through MVC's own AuthorizeFilter rather
        // than the middleware.
        action.Filters.Add(new AllowAnonymousFilter());
    }
}
