using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OfficeAschiApi.Data;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.McpBridge;

/// <summary>
/// Scans all [ApiController] classes and registers each action as an McpServerTool.
/// Any new controller/action is automatically exposed as an MCP tool.
/// Tools invoke controller methods directly via DI — no internal HTTP calls.
/// </summary>
public static class ControllerMcpBridge
{
    public static IServiceCollection AddToolsFromControllers(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var controllerTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                && t.GetCustomAttribute<ApiControllerAttribute>() != null);

        foreach (var controllerType in controllerTypes)
        {
            var baseRoute = controllerType.GetCustomAttribute<RouteAttribute>()?.Template ?? "";
            var controllerName = controllerType.Name.Replace("Controller", "");

            var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var (httpVerb, routeTemplate) = GetHttpInfo(method);
                if (httpVerb == null) continue;

                var fullRoute = BuildFullRoute(baseRoute, routeTemplate, controllerName);
                var xmlMember = GetXmlMember(method);
                var summary = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                    ?? xmlMember?.Element("summary")?.Value.Trim();
                var responses = GetXmlResponses(xmlMember);
                var description = BuildToolDescription(summary, httpVerb, fullRoute, responses);
                var requiresAuth = method.GetCustomAttribute<Middleware.TotpAuthAttribute>() != null;

                var toolName = ToSnakeCase($"{controllerName}_{method.Name}");
                var parameters = ExtractParameters(method, fullRoute, requiresAuth, xmlMember);

                var inputSchema = BuildJsonSchema(parameters);
                var tool = new Tool
                {
                    Name = toolName,
                    Description = description,
                    InputSchema = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(inputSchema))
                };

                var endpointTool = new ApiEndpointTool(tool, controllerType, method, parameters, requiresAuth);
                services.AddSingleton<McpServerTool>(endpointTool);
            }
        }

        return services;
    }

    private static (string? verb, string? template) GetHttpInfo(MethodInfo method)
    {
        if (method.GetCustomAttribute<HttpGetAttribute>() is { } get)
            return ("GET", get.Template);
        if (method.GetCustomAttribute<HttpPostAttribute>() is { } post)
            return ("POST", post.Template);
        if (method.GetCustomAttribute<HttpPutAttribute>() is { } put)
            return ("PUT", put.Template);
        if (method.GetCustomAttribute<HttpDeleteAttribute>() is { } del)
            return ("DELETE", del.Template);
        if (method.GetCustomAttribute<HttpPatchAttribute>() is { } patch)
            return ("PATCH", patch.Template);
        return (null, null);
    }

    private static string BuildFullRoute(string baseRoute, string? actionTemplate, string controllerName)
    {
        var route = baseRoute.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(actionTemplate))
        {
            if (actionTemplate.StartsWith('/'))
                route = actionTemplate.TrimStart('/');
            else
                route = $"{route}/{actionTemplate}";
        }

        return route;
    }

    internal static List<ToolParameter> ExtractParameters(MethodInfo method, string fullRoute, bool requiresAuth, XElement? xmlMember)
    {
        var parameters = new List<ToolParameter>();

        var routeParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(fullRoute, @"\{(\w+)\}"))
        {
            routeParams.Add(m.Groups[1].Value);
        }

        foreach (var p in method.GetParameters())
        {
            if (p.ParameterType == typeof(CancellationToken)) continue;

            if (routeParams.Contains(p.Name!))
            {
                parameters.Add(new ToolParameter(p.Name!, MapJsonType(p.ParameterType), ToolParameterSource.Route, true,
                    GetParamDescription(p, xmlMember)));
                continue;
            }

            if (p.GetCustomAttribute<FromQueryAttribute>() != null
                || (!p.GetCustomAttributes().Any(a => a is FromBodyAttribute) && IsSimpleType(p.ParameterType)))
            {
                var isRequired = !IsNullable(p) && !p.HasDefaultValue;
                parameters.Add(new ToolParameter(p.Name!, MapJsonType(p.ParameterType), ToolParameterSource.Query, isRequired,
                    GetParamDescription(p, xmlMember)));
                continue;
            }

            if (p.GetCustomAttribute<FromBodyAttribute>() != null || !IsSimpleType(p.ParameterType))
            {
                foreach (var prop in p.ParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var isRequired = !IsNullable(prop) && prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() != null;
                    parameters.Add(new ToolParameter(
                        ToCamelCase(prop.Name),
                        MapJsonType(prop.PropertyType),
                        ToolParameterSource.Body,
                        isRequired,
                        GetPropDescription(prop)));
                }
                continue;
            }
        }

        if (requiresAuth)
        {
            parameters.Add(new ToolParameter("totpAuth", "string", ToolParameterSource.Auth, true,
                "TOTP auth string: 'manager:{teamId}:{code}' or 'reportee:{reporteeId}:{code}'"));
        }

        return parameters;
    }

    private static object BuildJsonSchema(List<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in parameters)
        {
            var prop = new Dictionary<string, object> { ["type"] = p.JsonType };
            if (!string.IsNullOrEmpty(p.Description))
                prop["description"] = p.Description;
            properties[p.Name] = prop;

            if (p.IsRequired)
                required.Add(p.Name);
        }

        return new
        {
            type = "object",
            properties,
            required
        };
    }

    internal static string MapJsonType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(int) || t == typeof(long)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(bool)) return "boolean";
        return "string";
    }

    internal static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(DateTimeOffset)
            || t == typeof(Guid) || t == typeof(TimeSpan);
    }

    private static bool IsNullable(ParameterInfo p)
    {
        if (Nullable.GetUnderlyingType(p.ParameterType) != null) return true;
        var ctx = new NullabilityInfoContext();
        return ctx.Create(p).WriteState == NullabilityState.Nullable;
    }

    private static bool IsNullable(PropertyInfo p)
    {
        if (Nullable.GetUnderlyingType(p.PropertyType) != null) return true;
        var ctx = new NullabilityInfoContext();
        return ctx.Create(p).WriteState == NullabilityState.Nullable;
    }

    private static string? GetParamDescription(ParameterInfo p, XElement? xmlMember)
    {
        return p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
            ?? GetXmlParamDescription(xmlMember, p.Name!)
            ?? Humanize(p.Name!);
    }

    private static string? GetPropDescription(PropertyInfo p)
    {
        return p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
            ?? Humanize(p.Name);
    }

    private static string? GetXmlSummary(MethodInfo method)
    {
        return GetXmlMember(method)?.Element("summary")?.Value.Trim();
    }

    private static XDocument? _cachedXmlDoc;
    private static string? _cachedXmlPath;

    private static XElement? GetXmlMember(MethodInfo method)
    {
        var xmlFile = Path.ChangeExtension(method.DeclaringType!.Assembly.Location, ".xml");
        if (!File.Exists(xmlFile)) return null;
        try
        {
            if (_cachedXmlDoc == null || _cachedXmlPath != xmlFile)
            {
                _cachedXmlDoc = XDocument.Load(xmlFile);
                _cachedXmlPath = xmlFile;
            }
            var memberName = $"M:{method.DeclaringType.FullName}.{method.Name}";
            return _cachedXmlDoc.Descendants("member")
                .FirstOrDefault(m => m.Attribute("name")?.Value.StartsWith(memberName) == true);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetXmlParamDescription(XElement? xmlMember, string paramName)
    {
        return xmlMember?.Elements("param")
            .FirstOrDefault(e => e.Attribute("name")?.Value == paramName)
            ?.Value.Trim();
    }

    private static List<(string code, string description)> GetXmlResponses(XElement? xmlMember)
    {
        if (xmlMember == null) return [];
        return xmlMember.Elements("response")
            .Select(e => (code: e.Attribute("code")?.Value ?? "", description: e.Value.Trim()))
            .Where(r => !string.IsNullOrEmpty(r.code))
            .ToList();
    }

    private static string BuildToolDescription(string? summary, string httpVerb, string fullRoute,
        List<(string code, string description)> responses)
    {
        var sb = new StringBuilder();
        sb.Append(summary ?? $"{httpVerb} {fullRoute}");

        if (responses.Count > 0)
        {
            sb.Append(" | Responses: ");
            sb.Append(string.Join("; ", responses.Select(r => $"{r.code}: {r.description}")));
        }

        return sb.ToString();
    }

    private static string Humanize(string pascalCase)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pascalCase.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascalCase[i]) && !char.IsUpper(pascalCase[i - 1]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpper(pascalCase[i]) : char.ToLower(pascalCase[i]));
        }
        return sb.ToString();
    }

    internal static string ToSnakeCase(string input)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            if (i > 0 && char.IsUpper(input[i]) && !char.IsUpper(input[i - 1]))
                sb.Append('_');
            sb.Append(char.ToLower(input[i]));
        }
        return sb.ToString();
    }

    private static string ToCamelCase(string input) =>
        string.IsNullOrEmpty(input) ? input : char.ToLower(input[0]) + input[1..];
}

internal enum ToolParameterSource { Route, Query, Body, Auth }

internal record ToolParameter(string Name, string JsonType, ToolParameterSource Source, bool IsRequired, string? Description);

/// <summary>
/// An McpServerTool that directly invokes controller action methods via DI.
/// No internal HTTP calls — controllers are instantiated and called in-process.
/// </summary>
internal class ApiEndpointTool : McpServerTool
{
    private readonly Tool _tool;
    private readonly Type _controllerType;
    private readonly MethodInfo _actionMethod;
    private readonly List<ToolParameter> _parameters;
    private readonly bool _requiresAuth;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ApiEndpointTool(Tool tool, Type controllerType, MethodInfo actionMethod,
        List<ToolParameter> parameters, bool requiresAuth)
    {
        _tool = tool;
        _controllerType = controllerType;
        _actionMethod = actionMethod;
        _parameters = parameters;
        _requiresAuth = requiresAuth;
    }

    public override Tool ProtocolTool => _tool;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        using var scope = request.Services!.CreateScope();
        var sp = scope.ServiceProvider;
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();

        // Create controller via DI
        var controller = (ControllerBase)ActivatorUtilities.CreateInstance(sp, _controllerType);

        // Set up HttpContext (needed for HttpContext.Items used by TOTP auth)
        var httpContext = new DefaultHttpContext { RequestServices = sp };

        // Handle TOTP authentication — validate inline (same logic as TotpAuthMiddleware)
        if (_requiresAuth)
        {
            if (!args.TryGetValue("totpAuth", out var authValue))
                return ErrorResult("totpAuth parameter is required for this operation");

            var authStr = authValue.ValueKind == JsonValueKind.String ? authValue.GetString()! : authValue.GetRawText();
            var parts = authStr.Split(':');
            if (parts.Length != 3)
                return ErrorResult("Invalid totpAuth format. Expected: manager:{teamId}:{code} or reportee:{reporteeId}:{code}");

            var entityType = parts[0].ToLowerInvariant();
            if (entityType != "manager" && entityType != "reportee")
                return ErrorResult("Invalid entity type. Must be 'manager' or 'reportee'");

            if (!int.TryParse(parts[1], out var entityId))
                return ErrorResult("Invalid entity ID in totpAuth");

            var totpCode = parts[2];

            // Validate TOTP against DB
            var db = sp.GetRequiredService<AppDbContext>();
            var totpService = sp.GetRequiredService<TotpService>();

            string? secret = entityType == "manager"
                ? await db.Teams.Where(t => t.Id == entityId).Select(t => t.ManagerTotpSecret).FirstOrDefaultAsync(cancellationToken)
                : await db.Reportees.Where(r => r.Id == entityId).Select(r => r.TotpSecret).FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrEmpty(secret))
                return ErrorResult("TOTP not set up for this entity");

            if (!totpService.ValidateTotp(secret, totpCode))
                return ErrorResult("Invalid TOTP code");

            httpContext.Items["TotpEntityType"] = entityType;
            httpContext.Items["TotpEntityId"] = entityId;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Build method arguments from MCP params
        var methodParams = _actionMethod.GetParameters();
        var methodArgs = new object?[methodParams.Length];

        for (int i = 0; i < methodParams.Length; i++)
        {
            var mp = methodParams[i];

            if (mp.ParameterType == typeof(CancellationToken))
            {
                methodArgs[i] = cancellationToken;
                continue;
            }

            // Body/complex type — reconstruct from flattened MCP params
            if (mp.GetCustomAttribute<FromBodyAttribute>() != null
                || !ControllerMcpBridge.IsSimpleType(mp.ParameterType))
            {
                var bodyDict = new Dictionary<string, JsonElement>();
                foreach (var bp in _parameters.Where(p => p.Source == ToolParameterSource.Body))
                {
                    if (args.TryGetValue(bp.Name, out var val))
                        bodyDict[bp.Name] = val;
                }
                methodArgs[i] = JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(bodyDict), mp.ParameterType, _jsonOptions);
                continue;
            }

            // Route or query param
            if (args.TryGetValue(mp.Name!, out var paramVal))
            {
                methodArgs[i] = ConvertJsonElement(paramVal, mp.ParameterType);
            }
            else if (mp.HasDefaultValue)
            {
                methodArgs[i] = mp.DefaultValue;
            }
            else
            {
                methodArgs[i] = mp.ParameterType.IsValueType
                    ? Activator.CreateInstance(mp.ParameterType)
                    : null;
            }
        }

        try
        {
            var result = _actionMethod.Invoke(controller, methodArgs);

            // Await if async
            if (result is Task task)
            {
                await task;
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                    result = taskType.GetProperty("Result")!.GetValue(task);
            }

            // Unwrap ActionResult<T> / IActionResult
            var (value, statusCode) = UnwrapResult(result);
            var json = JsonSerializer.Serialize(value, _jsonOptions);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
                IsError = statusCode >= 400
            };
        }
        catch (TargetInvocationException ex)
        {
            return ErrorResult(ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static (object? value, int statusCode) UnwrapResult(object? result)
    {
        if (result == null) return (null, 200);

        var type = result.GetType();

        // ActionResult<T> — has Result (IActionResult) and Value (T) properties
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            var actionResult = type.GetProperty("Result")!.GetValue(result);
            if (actionResult != null)
                return UnwrapResult(actionResult);
            return (type.GetProperty("Value")!.GetValue(result), 200);
        }

        // ObjectResult (Ok, Created, BadRequest, NotFound, Conflict, etc.)
        if (result is ObjectResult objectResult)
            return (objectResult.Value, objectResult.StatusCode ?? 200);

        // StatusCodeResult (NoContent, etc.)
        if (result is StatusCodeResult statusCodeResult)
            return (new { statusCode = statusCodeResult.StatusCode }, statusCodeResult.StatusCode);

        // ForbidResult
        if (result is ForbidResult)
            return (new { error = "Forbidden" }, 403);

        return (result, 200);
    }

    private static object? ConvertJsonElement(JsonElement element, Type targetType)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return null;

        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t == typeof(string)) return element.GetString();
        if (t == typeof(int)) return element.GetInt32();
        if (t == typeof(long)) return element.GetInt64();
        if (t == typeof(bool)) return element.GetBoolean();
        if (t == typeof(double)) return element.GetDouble();
        if (t == typeof(float)) return element.GetSingle();
        if (t == typeof(decimal)) return element.GetDecimal();
        if (t == typeof(DateOnly)) return DateOnly.Parse(element.GetString()!);
        if (t == typeof(DateTime)) return element.GetDateTime();
        if (t == typeof(Guid)) return element.GetGuid();

        return JsonSerializer.Deserialize(element.GetRawText(), targetType, _jsonOptions);
    }

    private static CallToolResult ErrorResult(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
