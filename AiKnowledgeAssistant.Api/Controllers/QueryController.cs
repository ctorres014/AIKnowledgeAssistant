using Microsoft.AspNetCore.Mvc;

namespace AiKnowledgeAssistant.Api.Controllers;

/// <summary>
/// Placeholder query endpoint. SPEC 01 fixes the contract shape only;
/// the RAG logic arrives in SPEC 03.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class QueryController : ControllerBase
{
    // POST /api/query
    [HttpPost]
    public IActionResult Post([FromBody] QueryRequest request)
        => StatusCode(StatusCodes.Status501NotImplemented);
}

/// <summary>Request contract for POST /api/query (shape only, see SPEC 01 data model).</summary>
public sealed record QueryRequest(string Question);
