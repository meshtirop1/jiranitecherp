using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace JiranisokoTech.Web;

/// <summary>
/// An empty choice in a select is "none", not a value that fails to parse.
/// </summary>
/// <remarks>
/// <b>Choosing "none" in a select refused the whole form, and said nothing.</b> A select bound
/// to an optional identifier — the department on the hire page, the project when raising work,
/// the assignee, a manager, a project's client — offers a first option of <c>value=""</c> for
/// "none". The browser posts that as an empty string. Blazor's form mapping reads the field
/// as a <c>Guid?</c>, cannot parse "", records a mapping error, and the form's valid-submit
/// handler never runs. None of these fields had a message to show the error beside, so the page
/// simply came back with nothing saved. The first person could not be added at all — they have
/// nobody to report to — and work could not be raised without a project, time could not be
/// logged against no project, and nobody could be unassigned. Thirteen fields on ten pages.
/// Found by the automation work building a joiner template, and confirmed in a browser.
///
/// Leaving the field out of the post is what "none" should mean, and the mapper handles that
/// correctly: the property keeps its default, which for a nullable is null. So for every form
/// field whose model property is a nullable value type, an empty posted value is removed
/// before anything reads the form. The fields are found once, by reflection over the pages'
/// bound models, so a page added next month is covered without anybody remembering this.
///
/// Only nullable value types. An empty text box bound to a string must still arrive as empty,
/// and an empty box bound to a non-nullable number must still be refused rather than silently
/// becoming its default — that silent default is a fault this codebase has already had once.
/// </remarks>
public static class BlankChoices
{
    /// <summary>Every posted field name bound to a nullable value type, across all pages.</summary>
    public static IReadOnlySet<string> Fields { get; } = Find();

    public static IApplicationBuilder UseBlankChoices(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);

                if (form.Any(field => IsBlankChoice(field.Key, field.Value)))
                {
                    var kept = form
                        .Where(field => !IsBlankChoice(field.Key, field.Value))
                        .ToDictionary(field => field.Key, field => field.Value);

                    context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(kept, form.Files)));
                }
            }

            await next();
        });

    private static bool IsBlankChoice(string name, StringValues value) =>
        Fields.Contains(name) && value.All(string.IsNullOrEmpty);

    private static HashSet<string> Find()
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var bound = typeof(BlankChoices).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .SelectMany(type => type.GetProperties(Any))
            .Where(property => property.GetCustomAttribute<SupplyParameterFromFormAttribute>() is not null);

        foreach (var model in bound)
        {
            var prefix = model.GetCustomAttribute<SupplyParameterFromFormAttribute>()!.Name ?? model.Name;

            foreach (var member in model.PropertyType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (member.CanWrite && Nullable.GetUnderlyingType(member.PropertyType) is not null)
                {
                    fields.Add($"{prefix}.{member.Name}");
                }
            }
        }

        return fields;
    }
}
