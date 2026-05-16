using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;
using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Tools;

public class CustomerToolService
{
    private readonly ICustomerRepository _customers;

    public CustomerToolService(ICustomerRepository customers)
    {
        _customers = customers;
    }

    [Description("Registra un nuevo cliente (exportador) en el sistema.")]
    public async Task<object> RegisterCustomerAsync(
        [Description("Nombre completo del cliente o representante")] string fullName,
        [Description("Correo electrónico")] string? email = null,
        [Description("Teléfono de contacto")] string? phone = null,
        [Description("Nombre de la empresa exportadora")] string? companyName = null,
        [Description("País de origen")] string? country = null,
        [Description("Dirección comercial o de contacto")] string? address = null,
        [Description("Número de documento o identificación fiscal/comercial")] string? documentNumber = null)
    {
        var customer = new Customer
        {
            FullName = fullName,
            Email = email,
            Phone = phone,
            CompanyName = companyName,
            Country = country,
            Address = address,
            DocumentNumber = documentNumber
        };
        var id = await _customers.CreateAsync(customer);
        return new
        {
            customerId = id,
            fullName,
            companyName,
            message = $"Cliente {fullName} registrado exitosamente con ID {id}."
        };
    }

    [Description("Busca clientes por nombre, email o nombre de empresa.")]
    public async Task<object> SearchCustomerAsync(
        [Description("Nombre, email o empresa a buscar")] string query)
    {
        var results = await _customers.SearchAsync(query);
        return new
        {
            count = results.Count(),
            customers = results.Select(c => new { c.Id, c.FullName, c.Email, c.Phone, c.CompanyName, c.Country, c.Address, c.DocumentNumber })
        };
    }
}

public static class CustomerTools
{
    public static AIFunction CreateRegisterCustomerTool(ICustomerRepository customers)
    {
        var service = new CustomerToolService(customers);
        var method = typeof(CustomerToolService).GetMethod(nameof(CustomerToolService.RegisterCustomerAsync))!;
        return AIFunctionFactory.Create(method, service, "register_customer", "Registra un nuevo cliente exportador y devuelve su customerId.");
    }

    public static AIFunction CreateSearchCustomerTool(ICustomerRepository customers)
    {
        var service = new CustomerToolService(customers);
        var method = typeof(CustomerToolService).GetMethod(nameof(CustomerToolService.SearchCustomerAsync))!;
        return AIFunctionFactory.Create(method, service, "search_customer", "Busca clientes por nombre, email o empresa.");
    }
}
