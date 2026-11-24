using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace SlayTheSpire2.LAN.Multiplayer.Helpers
{
    internal static class SceneAdoptionHelper
    {
        // Moves an instanced scene's nodes under another root and frees the scene's own root, so a screen of ours can
        // wear a game screen's layout. The nodes belong to the scene's root, and the game finds them by unique name
        // (%LoadingOverlay), which only resolves through the owner, so the new root takes over their ownership too.
        public static void Adopt(Node sceneRoot, Node newRoot)
        {
            var ownedNodes = new List<Node>();
            CollectOwnedNodes(sceneRoot, sceneRoot, ownedNodes);

            foreach (var child in sceneRoot.GetChildren())
            {
                child.Reparent(newRoot, false);
            }

            foreach (var node in ownedNodes)
            {
                node.Owner = newRoot;
            }

            sceneRoot.QueueFreeSafely();
        }

        private static void CollectOwnedNodes(Node node, Node owner, List<Node> ownedNodes)
        {
            foreach (var child in node.GetChildren())
            {
                if (child.Owner == owner)
                {
                    ownedNodes.Add(child);
                }

                CollectOwnedNodes(child, owner, ownedNodes);
            }
        }
    }
}
