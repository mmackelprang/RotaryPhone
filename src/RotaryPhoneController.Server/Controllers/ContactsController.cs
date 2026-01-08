using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core.Contacts;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContactsController : ControllerBase
{
    private readonly IContactService _contactService;
    private readonly ILogger<ContactsController> _logger;

    public ContactsController(IContactService contactService, ILogger<ContactsController> logger)
    {
        _contactService = contactService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        return Ok(_contactService.GetAllContacts());
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var contact = _contactService.GetContact(id);
        if (contact == null) return NotFound();
        return Ok(contact);
    }

    [HttpPost]
    public IActionResult Create([FromBody] Contact contact)
    {
        if (string.IsNullOrEmpty(contact.Id)) contact.Id = Guid.NewGuid().ToString();
        _contactService.AddContact(contact);
        return CreatedAtAction(nameof(Get), new { id = contact.Id }, contact);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] Contact contact)
    {
        if (id != contact.Id) return BadRequest();
        _contactService.UpdateContact(contact);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        _contactService.DeleteContact(id);
        return NoContent();
    }
    
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string query)
    {
        return Ok(_contactService.SearchContacts(query));
    }
}
