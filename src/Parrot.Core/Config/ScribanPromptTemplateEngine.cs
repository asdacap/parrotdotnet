using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Parrot.Config;

internal sealed class ScribanPromptTemplateEngine(string path, string text) : IPromptTemplateEngine
{
    private readonly Template _template = Parse(path, text);

    public string Render(ScriptObject arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateValue(arguments, path, new HashSet<object>(ReferenceEqualityComparer.Instance), cancellationToken);
        var context = new TemplateContext
        {
            StrictVariables = true,
            AutoIndent = false,
            EnableRelaxedMemberAccess = false,
            EnableRelaxedTargetAccess = false,
            EnableRelaxedIndexerAccess = false,
            CancellationToken = cancellationToken,
            LoopLimit = 0,
        };
        context.PushGlobal(arguments);
        try
        {
            return _template.Render(context);
        }
        catch (ScriptRuntimeException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException($"{path}.template: {exception.Message}", exception);
        }
    }

    private static Template Parse(string path, string text)
    {
        var template = Template.Parse(text, $"{path}.template");
        return template.HasErrors
            ? throw new InvalidDataException($"{path}.template: {template.Messages}")
            : template;
    }

    private static void ValidateValue(object? value, string path, HashSet<object> ancestors, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null or string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return;
        }

        if (!ancestors.Add(value))
        {
            throw new InvalidDataException($"{path} contains cyclic script values");
        }

        switch (value)
        {
            case ScriptObject scriptObject:
                foreach (var member in scriptObject)
                {
                    ValidateValue(member.Value, $"{path}.{member.Key}", ancestors, cancellationToken);
                }

                break;
            case ScriptArray scriptArray:
                foreach (var item in scriptArray)
                {
                    ValidateValue(item, path, ancestors, cancellationToken);
                }

                foreach (var member in scriptArray.GetMembers())
                {
                    if (scriptArray.TryGetValue(null, default, member, out var memberValue))
                    {
                        ValidateValue(memberValue, $"{path}.{member}", ancestors, cancellationToken);
                    }
                }

                break;
            default:
                throw new InvalidDataException($"{path} accepts only ScriptObject, ScriptArray, and primitive script values");
        }

        _ = ancestors.Remove(value);
    }
}
