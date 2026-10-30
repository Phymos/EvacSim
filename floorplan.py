"""Draw a simple floor plan: 1200x800, 4 rooms, central corridor, 2 exits."""
from PIL import Image, ImageDraw, ImageFont

W, H = 1200, 800
WALL = 8
BLACK = (0, 0, 0)
GREEN = (0, 180, 0)
DARK_GREEN = (0, 120, 0)
GRAY = (100, 100, 100)

# Building bounds
X0, Y0, X1, Y1 = 150, 100, 1050, 700
# Central horizontal corridor
CY_TOP, CY_BOT = 360, 440
MID_X = 600

img = Image.new("RGB", (W, H), "white")
draw = ImageDraw.Draw(img)

try:
    font = ImageFont.truetype("arial.ttf", 24)
    small = ImageFont.truetype("arial.ttf", 18)
except OSError:
    font = ImageFont.load_default()
    small = ImageFont.load_default()


def h_wall(y, x_start, x_end, gaps=()):
    """Horizontal wall with gaps (doors): gaps = [(gx0, gx1), ...]."""
    cur = x_start
    for gx0, gx1 in sorted(gaps):
        if gx0 > cur:
            draw.line([(cur, y), (gx0, y)], fill=BLACK, width=WALL)
        cur = max(cur, gx1)
    if cur < x_end:
        draw.line([(cur, y), (x_end, y)], fill=BLACK, width=WALL)


def v_wall(x, y_start, y_end, gaps=()):
    """Vertical wall with gaps (doors): gaps = [(gy0, gy1), ...]."""
    cur = y_start
    for gy0, gy1 in sorted(gaps):
        if gy0 > cur:
            draw.line([(x, cur), (x, gy0)], fill=BLACK, width=WALL)
        cur = max(cur, gy1)
    if cur < y_end:
        draw.line([(x, cur), (x, y_end)], fill=BLACK, width=WALL)


# --- Outer walls (left/right have exit gaps where corridor meets them) ---
h_wall(Y0, X0, X1)  # top
h_wall(Y1, X0, X1)  # bottom
v_wall(X0, Y0, Y1, gaps=[(CY_TOP, CY_BOT)])  # left, exit 1 gap
v_wall(X1, Y0, Y1, gaps=[(CY_TOP, CY_BOT)])  # right, exit 2 gap

# --- Corridor walls with door gaps into each room ---
door_xs = [(330, 410), (790, 870)]
h_wall(CY_TOP, X0, X1, gaps=door_xs)
h_wall(CY_BOT, X0, X1, gaps=door_xs)

# --- Room divider walls with door gaps ---
v_wall(MID_X, Y0, CY_TOP, gaps=[(200, 270)])   # top rooms divider
v_wall(MID_X, CY_BOT, Y1, gaps=[(530, 600)])   # bottom rooms divider

# --- Exits: green rectangles on outer wall gaps ---
draw.rectangle([X0 - 30, CY_TOP, X0 + 10, CY_BOT], fill=GREEN, outline=DARK_GREEN, width=3)
draw.rectangle([X1 - 10, CY_TOP, X1 + 30, CY_BOT], fill=GREEN, outline=DARK_GREEN, width=3)

# --- Labels ---
draw.text((X0 + 110, Y0 + 110), "Room 1", fill=GRAY, font=font)
draw.text((MID_X + 170, Y0 + 110), "Room 2", fill=GRAY, font=font)
draw.text((X0 + 110, Y1 - 70), "Room 3", fill=GRAY, font=font)
draw.text((MID_X + 170, Y1 - 70), "Room 4", fill=GRAY, font=font)
draw.text((MID_X - 60, (CY_TOP + CY_BOT) // 2 - 14), "Corridor", fill=GRAY, font=small)
draw.text((X0 - 110, (CY_TOP + CY_BOT) // 2 - 14), "EXIT 1", fill=DARK_GREEN, font=small)
draw.text((X1 + 40, (CY_TOP + CY_BOT) // 2 - 14), "EXIT 2", fill=DARK_GREEN, font=small)
draw.text((W // 2 - 70, 40), "Floor Plan", fill=BLACK, font=font)

img.save("floorplan.png")
print("Saved floorplan.png", img.size)
