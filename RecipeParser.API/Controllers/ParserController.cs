using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Domain.Models;
using RecipeParser.Domain.Requests;

namespace RecipeParser.Controllers;

[ApiController]
[Route("[controller]")]
public class ParserController(IRecipeParserService parser): ControllerBase
{
    /// <summary>
    /// Takes a URL to a recipe page and attempts to parse the recipe data from it.
    /// </summary>
    [HttpPost("Parse")]
    [ProducesResponseType<Recipe>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Parse([Required] [FromBody] ParseUrlRequest request, CancellationToken ct)
    {
        try
        {
            var result = await parser.ParseRecipeByUrl(request.Url, ct);
            return Ok(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "Recipe import timed out. Please try again or paste the recipe text." });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}