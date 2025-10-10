using System;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game;

public static class LongDistancePathfinderSimple
{
    private const int DEFAULT_PATHFIND_LENGTH = 10;
    private static int _destX, _destY, _totalDistance;
    private static bool _isPathfinding;
    private static PlayerMobile player;

    public static void Stop()
    {
        OnStop();
    }

    public static void SetDestination(int x, int y)
    {
        if (World.Instance == null) return;
        player = World.Instance.Player;

        _destX = x;
        _destY = y;
        _totalDistance = player.DistanceFrom(new Vector2(x, y));
        _isPathfinding = true;
        EventSink.OnPositionChanged += EventSinkOnOnPositionChanged;

        Log.Info($"Long distance pathfinding set. Distance: {_totalDistance}.");
        Update(); //Begin
    }

    private static void Update()
    {
        if (!_isPathfinding || player.Pathfinder.AutoWalking) return;

        Log.Info("Long distance pathfinding Update()");

        var dist = player.DistanceFrom(new Vector2(_destX, _destY));

        if (dist < 1)
        {
            OnStop();
            return;
        }

        Direction dir = DirectionHelper.CalculateDirection(player.X, player.Y, _destX, _destY);

        for (int i = DEFAULT_PATHFIND_LENGTH; i > 0; i--)
        {
            Log.Info($"Long distance trying at {i} distance.");
            GetPositionAtDistance(player.X, player.Y, dir, i, out int targetX, out int targetY);
            World.Instance.Map.GetMapZ(targetX, targetY, out var g, out var s);
            if (player.Pathfinder.WalkTo(targetX, targetY, Math.Max(g, s), 0))
                break;
        }

        if (!player.Pathfinder.AutoWalking)
            OnStop();
    }

    private static void OnStop()
    {
        var dist = player.DistanceFrom(new Vector2(_destX, _destY));
        GameActions.Print($"Long distance pathfinding stopped with {dist} tiles left. Initial distance was {_totalDistance}.");
        EventSink.OnPositionChanged -= EventSinkOnOnPositionChanged;
        _isPathfinding = false;
    }

    private static void EventSinkOnOnPositionChanged(object sender, PositionChangedArgs e)
    {
        Update();
    }

    private static void GetPositionAtDistance(int startX, int startY, Direction direction, int distance, out int x, out int y)
        {
            x = startX;
            y = startY;

            switch (direction & (Direction)7)
            {
                case Direction.North:
                    y -= distance;
                    break;
                case Direction.Right:
                    x += distance;
                    y -= distance;
                    break;
                case Direction.East:
                    x += distance;
                    break;
                case Direction.Down:
                    x += distance;
                    y += distance;
                    break;
                case Direction.South:
                    y += distance;
                    break;
                case Direction.Left:
                    x -= distance;
                    y += distance;
                    break;
                case Direction.West:
                    x -= distance;
                    break;
                case Direction.Up:
                    x -= distance;
                    y -= distance;
                    break;
            }
        }
}
