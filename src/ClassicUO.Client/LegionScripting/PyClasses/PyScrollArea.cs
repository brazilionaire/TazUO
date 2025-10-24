using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using Microsoft.Xna.Framework;

namespace ClassicUO.LegionScripting.PyClasses;

/// <summary>
/// Inherits from PyBaseControl
/// </summary>
/// <param name="scrollArea"></param>
public class PyScrollArea(ScrollArea scrollArea) : PyBaseControl(scrollArea) { }
