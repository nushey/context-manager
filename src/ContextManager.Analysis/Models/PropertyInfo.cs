namespace ContextManager.Analysis.Models;

public sealed record PropertyInfo(string Name, string Type, string Access, bool? IsRequired = null);
