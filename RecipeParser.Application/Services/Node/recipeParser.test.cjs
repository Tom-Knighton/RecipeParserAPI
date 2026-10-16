const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseRecipe } = require('./recipeParser.cjs');

test('batch preserves ingredient order and parsed quantities', async () => {
    const result = await parseRecipe(['1 onion', '200 g flour'], []);
    assert.equal(result.ingredients[0].fullIngredient, '1 onion');
    assert.equal(result.ingredients[1].quantity, 200);
    assert.equal(result.instructions.length, 0);
});

test('batch preserves instruction order, timers and temperatures', async () => {
    const result = await parseRecipe([], ['Bake at 180 C for 20 minutes.', 'Rest for 5 minutes.']);
    assert.equal(result.instructions[0].timeItems[0].timeInSeconds, 1200);
    assert.equal(result.instructions[0].temperature, 180);
    assert.equal(result.instructions[1].timeItems[0].timeInSeconds, 300);
});

test('empty lines still have safe collection defaults', async () => {
    const result = await parseRecipe([''], ['']);
    assert.deepEqual(result.ingredients[0].alternativeQuantities, []);
    assert.deepEqual(result.instructions[0].timeItems, []);
});
