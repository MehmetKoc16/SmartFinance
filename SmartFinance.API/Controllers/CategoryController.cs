using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.Category;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers{
    [ApiController]
    [Route("api/[controller]")]
    public class CategoryController : ControllerBase
    {
        private readonly ICategoryService _categoryService;

        public CategoryController(ICategoryService categoryService)
        {
            _categoryService = categoryService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var categories = await _categoryService.GetAllCategoriesAsync();
            return Ok(categories);
        }
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var category = await _categoryService.GetCategoryByIdAsync(id);
            return Ok(category);
        }
        [HttpPost]
        public async Task<IActionResult> Create(CreateCategoryDto dto)
        {
            var category = await _categoryService.CreateCategoryAsync(dto);
            return CreatedAtAction(nameof(GetById), new {id=category.Id},category);
        }
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, CreateCategoryDto dto){
            await _categoryService.UpdateCategoryAsync(id, dto);
            return NoContent();
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id){
            await _categoryService.DeleteCategoryAsync(id);
            return NoContent();
        }
    }
}