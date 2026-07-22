namespace Ged.Core.Editing;

/// <summary>
/// The outcome of a geometry operation. Stock RED reports failures as modal
/// error boxes with fixed wording (e.g. "Faces aren't coplanar"); GED preserves
/// that exact wording in <see cref="Message"/> so the UI can surface it as a
/// non-modal toast while the operation itself never corrupts the model.
/// </summary>
public readonly record struct OpResult(bool Success, string Message)
{
    /// <summary>
    /// How many faces the op auto-triangulated to keep them planar (RED-parity edit-time planarity
    /// guard; see <see cref="FacePlanarizer"/>). Zero for ops that changed no face's flatness.
    /// </summary>
    public int FacesTriangulated { get; init; }

    public static OpResult Ok(string message = "") => new(true, message);

    public static OpResult Fail(string message) => new(false, message);

    public static implicit operator bool(OpResult r) => r.Success;
}
