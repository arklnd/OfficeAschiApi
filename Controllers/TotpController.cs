using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TotpController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TotpService _totpService;

    public TotpController(AppDbContext db, TotpService totpService)
    {
        _db = db;
        _totpService = totpService;
    }

    public record TotpVerifyRequest(string EntityType, int EntityId, string Code);
    public record TotpVerifyResponse(bool Valid);

    /// <summary>
    /// Verify a TOTP code against the server-stored secret (no browser secret needed).
    /// </summary>
    /// <param name="request">Entity type (manager or reportee), entity ID, and the 6-digit TOTP code.</param>
    /// <response code="200">Returns whether the TOTP code is valid.</response>
    /// <response code="400">Invalid entity type.</response>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(TotpVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TotpVerifyResponse>> Verify([FromBody] TotpVerifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length != 6)
            return Ok(new TotpVerifyResponse(false));

        string? secret = null;
        if (request.EntityType == "manager")
        {
            secret = await _db.Teams
                .Where(t => t.Id == request.EntityId)
                .Select(t => t.ManagerTotpSecret)
                .FirstOrDefaultAsync();
        }
        else if (request.EntityType == "reportee")
        {
            secret = await _db.Reportees
                .Where(r => r.Id == request.EntityId)
                .Select(r => r.TotpSecret)
                .FirstOrDefaultAsync();
        }
        else
        {
            return BadRequest(new { error = "EntityType must be 'manager' or 'reportee'" });
        }

        if (string.IsNullOrEmpty(secret))
            return Ok(new TotpVerifyResponse(false));

        var valid = _totpService.ValidateTotp(secret, request.Code);
        return Ok(new TotpVerifyResponse(valid));
    }
}
