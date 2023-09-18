/* The entirety of this script was written by Joshua Fratis */

using Loot;
using Networking;
using UnityEngine;
using UnityEngine.Serialization;
using Utility;

namespace ScriptableObjects.Crafting
{
    [CreateAssetMenu(menuName = "ScriptableSingleton/CraftingSystem")]
    public class CraftingSystem: SingletonScriptableObject<CraftingSystem>
    {
        public bool debugging = true;
        
        public GameObject prefab;
        private GameObject _instance;
        private bool _instanceExists;

        public static bool Craft(uint recipeId)
        {
            // Get Recipe
            var recipe = RecipeBook.GetRecipe(recipeId);
            return Craft(recipe);
        }

        public static bool Craft(RecipeBook.Recipe recipe)
        {
            // Check Recipe
            if (recipe == null)
            {
                Debug.LogError("Recipe does not exist for crafting.");
                return false;
            }
            if (Instance.debugging) Debug.Log("[DEBUG CS] Crafting " + recipe.Product.description.name);
            
            // Get Player
            var player = NetworkSessionManager.Singleton.localNetworkPlayer;
            if (!player)
            {
                Debug.LogError("Player does not exist for crafting.");
                return false;
            }
            var networkInventory = player.GetComponent<NetworkInventory>();
            if (!networkInventory)
            {
                Debug.LogError("Networked Inventory does not exist for crafting.");
                return false;
            }
            
            // Get Inventory
            var inventory = networkInventory.Inventory;
            if (inventory == null)
            {
                Debug.LogError("Inventory does not exist for crafting.");
                return false;
            }

            // Check for Ingredients
            if (Instance.debugging) Debug.Log("[DEBUG CS] Checking for Ingredients: ");
            foreach (var ingredient in recipe.Ingredients)
            {
                if (Instance.debugging) Debug.Log("[DEBUG CS] " + ingredient.Item.description.name);
                if (!inventory.HasItemStack(ingredient.Item, ingredient.Quantity))
                {
                    if (Instance.debugging) Debug.Log("[DEBUG CS] not enough " + ingredient.Item.description.name);
                    return false;
                }
            }

            // Consume Ingredients
            foreach (var ingredient in recipe.Ingredients)
            {
                if (Instance.debugging) Debug.Log("[DEBUG CS] Consuming " + ingredient.Item.description.name);
                var count = inventory.RequestConsumeItemStack(ingredient.Item, ingredient.Quantity);
                if (Instance.debugging) Debug.Log("[DEBUG CS] " + count + " consumed");
            }
        
            // Produce Product
            if (Instance.debugging) Debug.Log("[DEBUG CS] Producing " + recipe.Product.description.name);
            var itemInstance = Inventory.CreateItemInstance(recipe.Product.itemRegistryID, 1);
            inventory.AddItem(itemInstance);
            return true;
        }
        
        public static void ToggleCraftingMenu()
        {
            if (Instance._instanceExists)
            {
                Destroy(Instance._instance);
                Instance._instanceExists = false;
            }
            else
            {
                Instantiate(Instance.prefab);
                Instance._instanceExists = true;
            }
        }
    }
}
