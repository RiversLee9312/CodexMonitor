using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json;
using Acornima;
using Acornima.Ast;

namespace CodexMonitor.Core;

public sealed record ProgramToolCall(string Name, string Input, bool ArgumentsResolved);
public sealed record ToolProgram(IReadOnlyList<ProgramToolCall> Calls, bool Complete)
{
    private static readonly ConditionalWeakTable<string, ToolProgram> Cache = new();
    private static readonly ToolProgram Empty = new([], false);
    public static ToolProgram Parse(string source) => Cache.GetValue(source, Analyze);
    private static ToolProgram Analyze(string source)
    {
        // Parse syntax only. No transcript JavaScript, getter, regular expression or tool is executed.
        if (source.Length > 512_000 || !source.Contains("tools", StringComparison.Ordinal)) return Empty;
        try
        {
            var tree = new Parser(new ParserOptions { AllowAwaitOutsideFunction = true }).ParseScript(source);
            var reader = new Reader(source);
            reader.Read(tree, new(StringComparer.Ordinal), 0);
            return new(reader.Calls.ToArray(), reader.Complete);
        }
        catch (Exception ex) when (ex is ParseErrorException or InvalidOperationException or InsufficientExecutionStackException or ArgumentException)
        { return Empty; }
    }

    private abstract record V;
    private sealed record Unknown : V;
    private sealed record RuntimeValue : V;
    private sealed record Scalar(JsonNode? Data) : V;
    private sealed record ObjectValue(Dictionary<string, V> Members) : V;
    private sealed record ArrayValue(List<V> Items) : V;
    private sealed record Reference(string Name) : V;
    private sealed record ArrayMethod(ArrayValue Array, string Name) : V;
    private sealed record ThenMethod(V Value) : V;
    private sealed record FunctionValue(IFunction Function, Dictionary<string, V> Scope) : V;
    private static readonly V Missing = new Unknown();
    private sealed class Reader(string source)
    {
        public List<ProgramToolCall> Calls { get; } = [];
        public bool Complete { get; private set; } = true;
        private int _steps;
        private bool _returned;
        private V _return = Missing;
        private readonly Stack<HashSet<string>> _locals = new();
        private string Slice(Node node) => source[node.Start..node.End];
        private bool Budget(int depth)
        {
            if (++_steps <= 16_000 && depth < 80 && Calls.Count < 128) return true;
            Complete = false; return false;
        }
        public V Read(Node? node, Dictionary<string, V> env, int depth)
        {
            if (node is null || !Budget(depth)) return Missing;
            V R(Node? child) => Read(child, env, depth + 1);
            switch (node)
            {
                case Identifier id:
                    return env.TryGetValue(id.Name, out var bound) ? bound : id.Name is "tools" or "Promise" or "JSON" or "text" or "notify" or "image" or "audio" or "generatedImage" ? new Reference(id.Name) : Missing;
                case Literal literal:
                    return literal.Value switch { string s => new Scalar(JsonValue.Create(s)), bool b => new Scalar(JsonValue.Create(b)), double n when double.IsFinite(n) => new Scalar(JsonValue.Create(n)), int n => new Scalar(JsonValue.Create(n)), null => new Scalar(null), _ => Missing };
                case AwaitExpression wait: return R(wait.Argument);
                case ChainExpression chain: return R(chain.Expression);
                case ParenthesizedExpression parens: return R(parens.Expression);
                case TemplateLiteral template:
                    var str = "";
                    for (var i = 0; i < template.Quasis.Count; i++)
                    {
                        str += template.Quasis[i].Value.Cooked;
                        if (i < template.Expressions.Count)
                        { var part = R(template.Expressions[i]); if (!TryText(part, out var t)) return Missing; str += t; }
                    }
                    return new Scalar(JsonValue.Create(str));
                case ObjectExpression obj:
                    var members = new Dictionary<string, V>(StringComparer.Ordinal);
                    foreach (var child in obj.Properties)
                    {
                        if (child is SpreadElement spread && R(spread.Argument) is ObjectValue more)
                        { foreach (var pair in more.Members) members[pair.Key] = pair.Value; }
                        else if (child is Property property && !property.Method && property.Kind == PropertyKind.Init)
                        {
                            var key = property.Computed ? R(property.Key) : new Scalar(JsonValue.Create(property.Key is Identifier keyId ? keyId.Name : (property.Key as Literal)?.Value?.ToString()));
                            if (!TryText(key, out var keyText)) return Missing;
                            members[keyText] = R(property.Value);
                        }
                        else return Missing;
                    }
                    return new ObjectValue(members);
                case ArrayExpression array:
                    var values = new List<V>();
                    foreach (var element in array.Elements)
                        if (element is SpreadElement spread)
                        { if (R(spread.Argument) is not ArrayValue tail) return Missing; values.AddRange(tail.Items); }
                        else values.Add(R(element));
                    return new ArrayValue(values);
                case MemberExpression member:
                    var receiver = R(member.Object);
                    var keyValue = member.Computed ? R(member.Property) : new Scalar(JsonValue.Create((member.Property as Identifier)?.Name));
                    return TryText(keyValue, out var keyName) ? Member(receiver, keyName) : Missing;
                case ArrowFunctionExpression arrow: return new FunctionValue(arrow, env);
                case FunctionExpression function: return new FunctionValue(function, env);
                case FunctionDeclaration function:
                    if (function.Id is not null) env[function.Id.Name] = new FunctionValue(function, env);
                    return Missing;
                case VariableDeclaration declaration:
                    foreach (var item in declaration.Declarations)
                    { if (item.Id is Identifier local && _locals.TryPeek(out var locals)) locals.Add(local.Name); Bind(item.Id, R(item.Init), env, depth + 1); }
                    return Missing;
                case AssignmentExpression assignment:
                    if (assignment.Left is Identifier outer && _locals.TryPeek(out var knownLocals) && !knownLocals.Contains(outer.Name)) Complete = false;
                    var assigned = R(assignment.Right);
                    var assignmentOp = source[assignment.Left.End..assignment.Right.Start].Trim();
                    if (assignmentOp != "=")
                    {
                        var old = R(assignment.Left);
                        if (assignmentOp == "+=" && Number(old, out var oldNumber) && Number(assigned, out var addedNumber)) assigned = new Scalar(JsonValue.Create(oldNumber + addedNumber));
                        else if (assignmentOp == "+=" && (IsString(old) || IsString(assigned)) && TryText(old, out var prefix) && TryText(assigned, out var suffix)) assigned = new Scalar(JsonValue.Create(prefix + suffix));
                        else { Complete = false; assigned = Missing; }
                    }
                    Bind(assignment.Left, assigned, env, depth + 1); return assigned;
                case ReturnStatement ret: _return = R(ret.Argument); _returned = true; return _return;
                case CallExpression call: return Call(call, env, depth + 1);
                case SequenceExpression sequence:
                    V result = Missing; foreach (var expression in sequence.Expressions) result = R(expression); return result;
                case UnaryExpression unary:
                    var operand = R(unary.Argument); var unaryOp = source[unary.Start..unary.Argument.Start].Trim();
                    if (unaryOp is "-" or "+" && Number(operand, out var numeric)) return new Scalar(JsonValue.Create(unaryOp == "-" ? -numeric : numeric));
                    if (unaryOp == "!" && operand is Scalar) return new Scalar(JsonValue.Create(!Truth(operand)));
                    return Missing;
                case BinaryExpression binary:
                    var op = source[binary.Left.End..binary.Right.Start].Trim();
                    var left = R(binary.Left);
                    if (op is "&&" or "||" or "??")
                    {
                        if (left is not Scalar scalar)
                        { if (binary.Right is not Literal) Complete = false; return new RuntimeValue(); }
                        var useRight = op == "??" ? scalar.Data is null : op == "&&" ? Truth(left) : !Truth(left);
                        return useRight ? R(binary.Right) : left;
                    }
                    var right = R(binary.Right);
                    if (op == "+" && TryText(left, out var ls) && TryText(right, out var rs))
                    {
                        if (Number(left, out var ln) && Number(right, out var rn)) return new Scalar(JsonValue.Create(ln + rn));
                        if (IsString(left) || IsString(right)) return new Scalar(JsonValue.Create(ls + rs));
                        return Missing;
                    }
                    if (op is "===" or "==" or "!==" or "!=" && left is Scalar l && right is Scalar r)
                    {
                        if (op is "==" or "!=" && l.Data?.GetValueKind() != r.Data?.GetValueKind()) return Missing;
                        return new Scalar(JsonValue.Create(JsonNode.DeepEquals(l.Data, r.Data) == (op is "===" or "==")));
                    }
                    return Missing;
                case ConditionalExpression conditional:
                    var test = R(conditional.Test);
                    if (test is not Scalar) { Complete = false; return Missing; }
                    return R(Truth(test) ? conditional.Consequent : conditional.Alternate);
                case IfStatement conditional:
                    var condition = R(conditional.Test);
                    if (condition is not Scalar) { Complete = false; Invalidate(conditional, env); return Missing; }
                    return R(Truth(condition) ? conditional.Consequent : conditional.Alternate);
                case ForOfStatement loop:
                    if (R(loop.Right) is not ArrayValue items || items.Items.Count > 128) { Complete = false; Invalidate(loop, env); return Missing; }
                    foreach (var value in items.Items)
                    {
                        var local = new Dictionary<string, V>(env, StringComparer.Ordinal);
                        var target = loop.Left is VariableDeclaration d ? d.Declarations[0].Id : loop.Left;
                        Bind(target, value, local, depth + 1); Read(loop.Body, local, depth + 1);
                        if (_returned) break;
                    }
                    return Missing;
                case BlockStatement block:
                    var scope = new Dictionary<string, V>(env, StringComparer.Ordinal);
                    var declared = block.Body.OfType<VariableDeclaration>().SelectMany(d => d.Declarations).SelectMany(d => Names(d.Id)).ToHashSet(StringComparer.Ordinal);
                    foreach (var statement in block.Body) { Read(statement, scope, depth + 1); if (_returned) break; }
                    foreach (var name in env.Keys.ToArray()) if (!declared.Contains(name)) env[name] = scope[name];
                    return Missing;
                case Acornima.Ast.Program program:
                    foreach (var statement in program.Body) { R(statement); if (_returned) break; }
                    return Missing;
                case ExpressionStatement expression: return R(expression.Expression);
                case EmptyStatement: return Missing;
                default:
                    // Do not traverse a dynamic branch or a function that was never invoked.
                    Complete = false; Invalidate(node, env); return Missing;
            }
        }
        private V Call(CallExpression call, Dictionary<string, V> env, int depth)
        {
            var callee = Read(call.Callee, env, depth + 1);
            var arguments = new List<V>();
            foreach (var arg in call.Arguments)
                if (arg is SpreadElement spread)
                { if (Read(spread.Argument, env, depth + 1) is not ArrayValue tail) { Complete = false; return Missing; } arguments.AddRange(tail.Items); }
                else arguments.Add(Read(arg, env, depth + 1));
            if (callee is FunctionValue function) return Invoke(function, arguments, depth + 1);
            if (callee is Reference reference)
            {
                if (reference.Name.StartsWith("tools.", StringComparison.Ordinal))
                {
                    JsonNode? argumentJson = null;
                    var resolved = arguments.Count == 1 && Json(arguments[0], out argumentJson);
                    var input = resolved ? argumentJson?.ToJsonString() ?? "null" : string.Join(", ", call.Arguments.Select(Slice));
                    // Custom tools receive a free-form string rather than a JSON-quoted string.
                    if (resolved && arguments[0] is Scalar scalar && scalar.Data is JsonValue value && value.TryGetValue<string>(out var text)) input = text;
                    Calls.Add(new(reference.Name[6..], input, resolved));
                    if (!resolved) Complete = false;
                    return new RuntimeValue();
                }
                if (reference.Name is "Promise.all" or "Promise.allSettled") return arguments.FirstOrDefault() ?? Missing;
                if (reference.Name is "Promise.resolve") return arguments.FirstOrDefault() ?? Missing;
                if (reference.Name == "JSON.stringify") return arguments.Count > 0 && Json(arguments[0], out var json) ? new Scalar(JsonValue.Create(json?.ToJsonString() ?? "null")) : new RuntimeValue();
                if (reference.Name == "JSON.parse" && arguments.FirstOrDefault() is Scalar { Data: JsonValue encoded } && encoded.TryGetValue<string>(out var serialized))
                { try { return FromJson(JsonNode.Parse(serialized)); } catch (System.Text.Json.JsonException) { Complete = false; return Missing; } }
                if (reference.Name is "text" or "notify" or "image" or "audio" or "generatedImage") return Missing;
            }
            if (callee is ThenMethod then && arguments.FirstOrDefault() is FunctionValue continuation) return Invoke(continuation, [then.Value], depth + 1);
            if (callee is ArrayMethod { Name: "map" or "forEach" } method && arguments.FirstOrDefault() is FunctionValue callback)
            {
                if (method.Array.Items.Count > 128) { Complete = false; return Missing; }
                var result = new List<V>();
                for (var i = 0; i < method.Array.Items.Count; i++) result.Add(Invoke(callback, [method.Array.Items[i], new Scalar(JsonValue.Create(i)), method.Array], depth + 1));
                return new ArrayValue(result);
            }
            // Unknown calls can mutate bindings. Retain the program and do not invent their result.
            Complete = false; Invalidate(call, env); return Missing;
        }
        private V Invoke(FunctionValue fn, List<V> args, int depth)
        {
            if (!Budget(depth)) return Missing;
            if (fn.Function.Generator) { Complete = false; return Missing; }
            var local = new Dictionary<string, V>(fn.Scope, StringComparer.Ordinal);
            _locals.Push(fn.Function.Params.SelectMany(Names).ToHashSet(StringComparer.Ordinal));
            for (var i = 0; i < fn.Function.Params.Count; i++) Bind(fn.Function.Params[i], i < args.Count ? args[i] : Missing, local, depth + 1);
            var wasReturned = _returned; var oldReturn = _return; _returned = false; _return = Missing;
            var result = Read(fn.Function.Body, local, depth + 1);
            if (_returned) result = _return;
            _returned = wasReturned; _return = oldReturn; _locals.Pop(); return result;
        }
        private void Bind(Node target, V value, Dictionary<string, V> env, int depth)
        {
            if (!Budget(depth)) return;
            switch (target)
            {
                case Identifier id: env[id.Name] = value; break;
                case AssignmentPattern pattern: Bind(pattern.Left, value is Unknown ? Read(pattern.Right, env, depth + 1) : value, env, depth + 1); break;
                case ObjectPattern obj:
                    foreach (var property in obj.Properties.OfType<Property>())
                    {
                        var key = property.Computed ? Read(property.Key, env, depth + 1) : new Scalar(JsonValue.Create(property.Key is Identifier id ? id.Name : (property.Key as Literal)?.Value?.ToString()));
                        Bind(property.Value, TryText(key, out var name) ? Member(value, name) : Missing, env, depth + 1);
                    }
                    break;
                case ArrayPattern array:
                    for (var i = 0; i < array.Elements.Count; i++) if (array.Elements[i] is { } item) Bind(item, Member(value, i.ToString(CultureInfo.InvariantCulture)), env, depth + 1);
                    break;
                default: Complete = false; break;
            }
        }
        private void Invalidate(Node node, Dictionary<string, V> env)
        {
            // A branch we cannot resolve must not leave a stale constant behind for later calls.
            foreach (var key in env.Keys.ToArray()) env[key] = Missing;
        }
        private static IEnumerable<string> Names(Node node)
        {
            var pending = new Stack<Node>(); pending.Push(node);
            while (pending.TryPop(out var item))
                switch (item)
                {
                    case Identifier id: yield return id.Name; break;
                    case ObjectPattern obj: foreach (var property in obj.Properties.OfType<Property>()) pending.Push(property.Value); break;
                    case ArrayPattern array: foreach (var element in array.Elements) if (element is not null) pending.Push(element); break;
                    case AssignmentPattern assignment: pending.Push(assignment.Left); break;
                }
        }
        private static V Member(V value, string key) => value switch
        {
            Reference reference => new Reference(reference.Name + "." + key),
            RuntimeValue when key == "then" => new ThenMethod(value),
            RuntimeValue => value,
            ArrayValue when key == "then" => new ThenMethod(value),
            ObjectValue obj when obj.Members.TryGetValue(key, out var member) => member,
            ArrayValue array when key == "length" => new Scalar(JsonValue.Create(array.Items.Count)),
            ArrayValue array when key is "map" or "forEach" => new ArrayMethod(array, key),
            ArrayValue array when int.TryParse(key, out var index) && index >= 0 && index < array.Items.Count => array.Items[index],
            _ => Missing
        };
        private static bool TryText(V value, out string text)
        {
            if (value is Scalar scalar) { text = scalar.Data is null ? "null" : scalar.Data is JsonValue j && j.TryGetValue<string>(out var s) ? s : scalar.Data.ToJsonString(); return true; }
            text = ""; return false;
        }
        private static V FromJson(JsonNode? node) => node switch
        {
            JsonArray array => new ArrayValue(array.Select(FromJson).ToList()),
            JsonObject obj => new ObjectValue(obj.ToDictionary(pair => pair.Key, pair => FromJson(pair.Value), StringComparer.Ordinal)),
            _ => new Scalar(node)
        };
        private static bool Number(V value, out double number)
        { number = 0; return value is Scalar { Data: JsonValue json } && json.TryGetValue<double>(out number); }
        private static bool IsString(V value) => value is Scalar { Data: JsonValue json } && json.TryGetValue<string>(out _);
        private static bool Truth(V value) => value is Scalar { Data: { } data } && data.ToJsonString() is not ("false" or "0" or "\"\"");
        private static bool Json(V value, out JsonNode? json)
        {
            json = null;
            switch (value)
            {
                case Scalar scalar: json = scalar.Data?.DeepClone(); return true;
                case ArrayValue array:
                    var result = new JsonArray(); foreach (var item in array.Items) { if (!Json(item, out var child)) return false; result.Add(child); } json = result; return true;
                case ObjectValue obj:
                    var map = new JsonObject(); foreach (var pair in obj.Members) { if (!Json(pair.Value, out var child)) return false; map[pair.Key] = child; } json = map; return true;
                default: return false;
            }
        }
    }
}
