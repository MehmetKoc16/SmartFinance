using Microsoft.EntityFrameworkCore;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;
using Scalar.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using SmartFinance.API.Infrastructure;
using SmartFinance.API.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SmartFinanceDbContext>(options=>
options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped(typeof(IGenericRepository<>),typeof(GenericRepository<>));
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
// OpenAPI'ye JWT güvenlik şeması ekle (Scalar'da Authorize butonu çıksın)
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IAuthService, AuthService>();

// JWT Authentication yapılandırması
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options=>{
    options.AddPolicy("AllowFrontend",policy=>{
        policy.WithOrigins("http://localhost:3000","http://localhost:5173").AllowAnyHeader().AllowAnyMethod();
    });
});
var app = builder.Build();
app.UseMiddleware<ExceptionMiddleware>();

if(app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();