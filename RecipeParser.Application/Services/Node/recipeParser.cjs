module.exports = {
    parseRecipe: async (ingredients, instructions, lang = 'en') => {
        const mod = await import('@jlucaspains/sharp-recipe-parser');
        return {
            ingredients: ingredients.map(line => ({ alternativeQuantities: [], ...mod.parseIngredient(line, lang, {}), fullIngredient: line })),
            instructions: instructions.map(line => ({ timeItems: [], ...mod.parseInstruction(line, lang, {}), fullInstruction: line }))
        };
    },
    parseIngredientLine: async (line, lang = 'en', options = {}) => {
        const mod = await import('@jlucaspains/sharp-recipe-parser');
        return { ...mod.parseIngredient(line, lang, options), fullIngredient: line }
    },
    parseInstructionLine: async (line, lang = 'en', options = {}) => {
        const mod = await import('@jlucaspains/sharp-recipe-parser');
        return { ...mod.parseInstruction(line, lang, options), fullInstruction: line }
    }
};