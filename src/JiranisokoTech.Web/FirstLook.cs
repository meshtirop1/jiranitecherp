namespace JiranisokoTech.Web;

/// <summary>
/// Whether this render is somebody arriving at a page, or the page handling their form.
/// </summary>
/// <remarks>
/// <b>This exists because of a fault that reported success on four pages.</b> A page that lets
/// somebody change a stored value seeds the control with the current one, so the select shows
/// who the work is assigned to now — and it must stop doing that once a form has been posted,
/// because the posted model holds the new value and overwriting it discards the change.
///
/// All four pages knew that. Each carried a comment saying so, and each guarded the seeding with
/// a <c>_loaded</c> field set on the first pass. Under static server rendering that guard does
/// nothing: <b>every request is a new component instance</b>, so on the POST the field is false
/// again, the seeding runs before the handler reads the model, and the handler is called with
/// the value that was already stored. The service then does what it is told, which is nothing,
/// and the page says "Assigned."
///
/// Found by assigning a work item to somebody in the running application, being told it had
/// worked, and looking at the row.
///
/// The <c>_loaded</c> guard is kept alongside this rather than replaced, because it is the
/// correct guard for an interactive page — there the component survives, there is no HttpContext
/// to ask, and re-seeding on every render would overwrite what somebody is typing.
/// </remarks>
public static class FirstLook
{
    /// <summary>
    /// True when nothing has been posted: a GET, or an interactive render.
    /// </summary>
    /// <remarks>
    /// A null context means there is no request to inspect, which is what an interactive
    /// component sees. It answers true there so that the caller's own first-pass guard decides,
    /// which is the right answer in a place where the instance lives long enough to have one.
    /// </remarks>
    public static bool ThisTime(HttpContext? context) =>
        context is null || HttpMethods.IsGet(context.Request.Method);
}
