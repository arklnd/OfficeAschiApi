using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

public class TotpSecurityDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var schemeRef = new OpenApiSecuritySchemeReference("TOTP", swaggerDoc);

        foreach (var pathItem in swaggerDoc.Paths.Values)
        {
            foreach (var operation in pathItem.Operations.Values)
            {
                operation.Security = new List<OpenApiSecurityRequirement>
                {
                    new OpenApiSecurityRequirement
                    {
                        { schemeRef, new List<string>() }
                    }
                };
            }
        }
    }
}
