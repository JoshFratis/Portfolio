/* The entirety of this script was written by Joshua Fratis */

using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using Utility;

namespace ScriptableObjects.Crafting
{
    [CreateAssetMenu(menuName = "ScriptableSingleton/RecipeBook")]
    public class RecipeBook : SingletonScriptableObject<RecipeBook>
    {
        public List<Recipe> Recipes;

        [Serializable] public class Recipe
        {
            public List<Ingredient> Ingredients;
            public ItemScriptableObject Product;
            [HideInInspector] public uint RecipeID;
        }

        [Serializable] public class Ingredient
        {
            public ItemScriptableObject Item;
            public int Quantity;
        }
        
        public void OnValidate()
        {
            for (var i = 0; i < Recipes.Count; i++)
            {
                Recipes[i].RecipeID = (uint)i;
            }
        }

        [CanBeNull]
        public static Recipe GetRecipe(uint id)
        {
            if (id < 0 || id > (uint)Instance.Recipes.Count - 1)
                return null;

            return Instance.Recipes[(int)id];
        }
    }
}
