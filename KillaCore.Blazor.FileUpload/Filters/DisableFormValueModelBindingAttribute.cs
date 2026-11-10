using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace KillaCore.Blazor.FileUpload.Filters;

/// <summary>
/// Stops MVC reading the request as a form during model binding.
/// <para>
/// Binding an <c>IFormFile</c> buffers the entire body before the action runs — to disk, for
/// anything over <c>MemoryBufferThreshold</c> — so the upload action could not see a single byte
/// until every byte had already been written once. Removing the form value providers leaves the
/// body untouched for <c>MultipartReader</c> to stream.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
