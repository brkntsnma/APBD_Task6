using APBD_Task6.DTOs;
using APBD_Task6.Services;
using Microsoft.AspNetCore.Mvc;

namespace APBD_Task6.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController(AppointmentService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
        var result = await service.GetAppointmentsAsync(status, patientLastName);
        return Ok(result);
    }

    [HttpGet("{idAppointment:int}")]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        var dto = await service.GetAppointmentByIdAsync(idAppointment);
        if (dto is null)
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto dto)
    {
        var (newId, error, statusCode) = await service.CreateAppointmentAsync(dto);
        return statusCode switch
        {
            201 => CreatedAtAction(nameof(GetAppointment), new { idAppointment = newId }, new { idAppointment = newId }),
            400 => BadRequest(new ErrorResponseDto { Message = error! }),
            404 => NotFound(new ErrorResponseDto { Message = error! }),
            409 => Conflict(new ErrorResponseDto { Message = error! }),
            _   => StatusCode(statusCode, new ErrorResponseDto { Message = error! })
        };
    }

    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto dto)
    {
        var (found, error, statusCode) = await service.UpdateAppointmentAsync(idAppointment, dto);
        if (!found)
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        return statusCode switch
        {
            200 => Ok(),
            400 => BadRequest(new ErrorResponseDto { Message = error! }),
            404 => NotFound(new ErrorResponseDto { Message = error! }),
            409 => Conflict(new ErrorResponseDto { Message = error! }),
            _   => StatusCode(statusCode, new ErrorResponseDto { Message = error! })
        };
    }

    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        var (found, error, statusCode) = await service.DeleteAppointmentAsync(idAppointment);
        if (!found)
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        if (statusCode == 409)
            return Conflict(new ErrorResponseDto { Message = error! });
        return NoContent();
    }
}