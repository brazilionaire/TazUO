using ClassicUO.Game.Data;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game;

public abstract class PathAlgorithm
{
    private static readonly Direction[] m_CalcDirections = new Direction[9]
    {
        Direction.Up,
        Direction.North,
        Direction.Right,
        Direction.West,
        Direction.North,
        Direction.East,
        Direction.Left,
        Direction.South,
        Direction.Down
    };
    public abstract bool CheckCondition(Vector3 p, Map.Map map, Vector3 start, Vector3 goal);

    public abstract Direction[] Find(Vector3 p, Map.Map map, Vector3 start, Vector3 goal);

    public Direction GetDirection(int xSource, int ySource, int xDest, int yDest)
    {
        int x = xDest + 1 - xSource;
        int y = yDest + 1 - ySource;
        int v = (y * 3) + x;

        if (v < 0 || v >= 9)
            return Direction.North;

        return m_CalcDirections[v];
    }
}
