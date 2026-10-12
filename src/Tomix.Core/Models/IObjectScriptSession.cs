namespace Tomix.Core.Models;

/// <summary>
/// Optional read capability: a session that can emit one object in a model file format, through
/// the provider's own serializer, so the fragment carries every property the model file would.
/// Handlers capability-check with a type test; a session without it cannot emit fragments.
/// </summary>
public interface IObjectScriptSession
{
    /// <summary>
    /// The object at <paramref name="path"/> (a snapshot path; <paramref name="kind"/> picks one
    /// when a path matches several) as TMDL or as its BIM JSON object.
    /// </summary>
    /// <exception cref="ObjectNotFoundException">Nothing at <paramref name="path"/> is of that kind.</exception>
    string ScriptObject(string path, ModelObjectKind kind, ObjectScriptFormat format);
}

public enum ObjectScriptFormat
{
    /// <summary>A TMDL fragment; a table child is wrapped in <c>ref table</c>, ready to paste into a table file.</summary>
    Tmdl,

    /// <summary>The object as it appears in a <c>model.bim</c> (TMSL JSON).</summary>
    Json
}
