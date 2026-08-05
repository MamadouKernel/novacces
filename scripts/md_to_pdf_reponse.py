"""
Convertit docs/reponse-besoins-api-app-agent.md en PDF, dans un style proche
du rapport original (besoins-api-app-agent.pdf) : titres bleus, blocs de code
sur fond gris, tableaux zébrés, cases à cocher pour le récapitulatif.

Usage : python scripts/md_to_pdf_reponse.py
Dépendances : pip install reportlab
"""
import re
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, HRFlowable, KeepTogether,
)
from reportlab.lib.enums import TA_LEFT
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "docs" / "reponse-besoins-api-app-agent.md"
OUT = ROOT / "docs" / "reponse-besoins-api-app-agent.pdf"

# Helvetica/Courier (base14) n'ont que l'encodage WinAnsi : pas de coche, de
# flèche ni de barre double (r‖s). Segoe UI (Windows) couvre ces glyphes —
# Helvetica reste en repli si la police système est absente (ex. Linux/CI).
FONTS_DIR = Path("C:/Windows/Fonts")
FONT_REGULAR, FONT_BOLD, FONT_ITALIC = "Helvetica", "Helvetica-Bold", "Helvetica-Oblique"
try:
    pdfmetrics.registerFont(TTFont("SegoeUI", str(FONTS_DIR / "segoeui.ttf")))
    pdfmetrics.registerFont(TTFont("SegoeUI-Bold", str(FONTS_DIR / "segoeuib.ttf")))
    pdfmetrics.registerFont(TTFont("SegoeUI-Italic", str(FONTS_DIR / "segoeuii.ttf")))
    pdfmetrics.registerFontFamily(
        "SegoeUI", normal="SegoeUI", bold="SegoeUI-Bold", italic="SegoeUI-Italic")
    FONT_REGULAR, FONT_BOLD, FONT_ITALIC = "SegoeUI", "SegoeUI-Bold", "SegoeUI-Italic"
except Exception:
    pass


def sanitize(text: str) -> str:
    """Neutralise les caractères absents des polices de base (emoji couleur),
    remplacés par leur équivalent textuel du même sens."""
    for emoji in ("🔴", "🟠", "🟡", "🟢"):
        text = text.replace(emoji + " ", "").replace(emoji, "")
    text = text.replace("‖", "||")
    text = text.replace("✅", "[OK]")
    text = text.replace("←", "<-").replace("→", "->")
    return text

NAVY = colors.HexColor("#0E2A3A")
AMBER = colors.HexColor("#F5A300")
GREY_BG = colors.HexColor("#F4F4F4")
GREY_LINE = colors.HexColor("#DDDDDD")
GREEN = colors.HexColor("#1E7A34")

styles = getSampleStyleSheet()

def style(name, **kw):
    base = dict(fontName=FONT_REGULAR, fontSize=10, leading=14, textColor=colors.HexColor("#1A1A1A"))
    base.update(kw)
    return ParagraphStyle(name, **base)

S_TITLE = style("Title2", fontName=FONT_BOLD, fontSize=18, leading=22, textColor=NAVY, spaceAfter=4)
S_META = style("Meta", fontName=FONT_ITALIC, fontSize=9, textColor=colors.HexColor("#555555"), spaceAfter=2)
S_H2 = style("H2", fontName=FONT_BOLD, fontSize=14, leading=18, textColor=NAVY, spaceBefore=16, spaceAfter=8)
S_H3 = style("H3", fontName=FONT_BOLD, fontSize=11.5, leading=15, textColor=NAVY, spaceBefore=10, spaceAfter=4)
S_BODY = style("Body", spaceAfter=6, alignment=TA_LEFT)
S_BULLET = style("Bullet", spaceAfter=3, leftIndent=14, bulletIndent=2)
S_CODE = style("Code", fontName="Courier", fontSize=8.3, leading=11, textColor=colors.HexColor("#1A1A1A"))
S_TABLE_CELL = style("TableCell", fontSize=8.7, leading=11)
S_TABLE_HEAD = style("TableHead", fontSize=8.7, leading=11, fontName=FONT_BOLD, textColor=colors.white)
S_QUOTE = style("Quote", fontSize=9.3, leading=13, textColor=colors.HexColor("#333333"), leftIndent=10,
                 borderColor=AMBER, borderWidth=0, spaceAfter=6)


def inline(text: str) -> str:
    """Markdown inline -> reportlab mini-XML (bold, code, links stripped)."""
    t = escape(text)
    t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"`([^`]+)`", r'<font face="Courier" size="8.7" backColor="#F0F0F0"> \1 </font>', t)
    t = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", t)
    return t


def parse_table(lines, i):
    rows = []
    while i < len(lines) and lines[i].strip().startswith("|"):
        row = [c.strip() for c in lines[i].strip().strip("|").split("|")]
        rows.append(row)
        i += 1
    # drop the separator row (---|---)
    if len(rows) > 1 and all(re.match(r"^:?-+:?$", c) for c in rows[1]):
        del rows[1]
    return rows, i


def build_table(rows):
    data = []
    for r_idx, row in enumerate(rows):
        style_ref = S_TABLE_HEAD if r_idx == 0 else S_TABLE_CELL
        data.append([Paragraph(inline(c), style_ref) for c in row])
    t = Table(data, hAlign="LEFT", repeatRows=1)
    cmds = [
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("GRID", (0, 0), (-1, -1), 0.5, GREY_LINE),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]
    for r in range(1, len(data)):
        if r % 2 == 0:
            cmds.append(("BACKGROUND", (0, r), (-1, r), GREY_BG))
    t.setStyle(TableStyle(cmds))
    return t


def build_code_block(code_lines):
    escaped = "<br/>".join(escape(l) if l.strip() else "&nbsp;" for l in code_lines)
    p = Paragraph(escaped, S_CODE)
    box = Table([[p]], colWidths=[16.5 * cm])
    box.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), GREY_BG),
        ("BOX", (0, 0), (-1, -1), 0.5, GREY_LINE),
        ("LEFTPADDING", (0, 0), (-1, -1), 8),
        ("RIGHTPADDING", (0, 0), (-1, -1), 8),
        ("TOPPADDING", (0, 0), (-1, -1), 6),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
    ]))
    return box


def is_hard_stop(stripped: str) -> bool:
    """Lignes qui ferment TOUJOURS le paragraphe/item en cours (jamais de
    fusion à travers elles)."""
    return (
        not stripped
        or stripped.startswith("```")
        or stripped.startswith("> ")
        or stripped.startswith("|")
        or stripped.startswith("# ")
        or stripped.startswith("## ")
        or stripped.startswith("### ")
    )


NUMBERED_RE = re.compile(r"^(\d+)\.\s+(.*)$")


def is_bullet_start(stripped: str) -> bool:
    return stripped.startswith("- ") or stripped.startswith("* ") or bool(NUMBERED_RE.match(stripped))


def join_wrapped_lines(md_text: str) -> str:
    """CommonMark : deux lignes consécutives sans ligne vide entre elles
    appartiennent au même bloc (simple retour à la ligne = espace) — y
    compris la suite indentée d'un item de liste. Sans cette passe, chaque
    retour à la ligne du .md source devenait un paragraphe séparé
    (espacement excessif) et coupait le markup **gras** à cheval sur deux
    lignes. Les blocs de code (```...```) sont préservés tels quels : leurs
    retours à la ligne sont significatifs.
    """
    lines = md_text.split("\n")
    out = []
    buffer = []
    prefix = ""
    in_code = False

    def flush():
        nonlocal prefix
        if buffer:
            out.append(prefix + " ".join(buffer))
            buffer.clear()
        prefix = ""

    for line in lines:
        stripped = line.strip()
        if stripped.startswith("```"):
            flush()
            in_code = not in_code
            out.append(line)
            continue
        if in_code:
            out.append(line)
            continue
        if is_hard_stop(stripped):
            flush()
            out.append(line)
            continue
        if is_bullet_start(stripped):
            flush()
            m = NUMBERED_RE.match(stripped)
            if m:
                prefix = f"{m.group(1)}. "
                buffer.append(m.group(2).strip())
            else:
                prefix = stripped[:2]  # "- " ou "* "
                buffer.append(stripped[2:].strip())
            continue
        buffer.append(stripped)
    flush()
    return "\n".join(out)


def build_flowables(md_text: str):
    lines = join_wrapped_lines(md_text).split("\n")
    story = []
    i = 0
    first_h1 = True
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        if stripped.startswith("```"):
            i += 1
            code_lines = []
            while i < len(lines) and not lines[i].strip().startswith("```"):
                code_lines.append(lines[i])
                i += 1
            i += 1  # skip closing fence
            story.append(build_code_block(code_lines))
            story.append(Spacer(1, 8))
            continue

        if stripped.startswith("> "):
            quote_lines = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                quote_lines.append(lines[i].strip().lstrip(">").strip())
                i += 1
            text = " ".join(quote_lines)
            box = Table([[Paragraph(inline(text), S_QUOTE)]], colWidths=[16.5 * cm])
            box.setStyle(TableStyle([
                ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor("#FFF7E8")),
                ("LINEBEFORE", (0, 0), (0, -1), 3, AMBER),
                ("LEFTPADDING", (0, 0), (-1, -1), 10),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                ("RIGHTPADDING", (0, 0), (-1, -1), 10),
            ]))
            story.append(box)
            story.append(Spacer(1, 8))
            continue

        if stripped.startswith("|"):
            rows, i = parse_table(lines, i)
            story.append(build_table(rows))
            story.append(Spacer(1, 10))
            continue

        if stripped.startswith("# "):
            story.append(Paragraph(inline(stripped[2:]), S_TITLE))
            story.append(HRFlowable(width="100%", thickness=1.2, color=NAVY, spaceAfter=10))
            i += 1
            continue

        if stripped.startswith("## "):
            story.append(Paragraph(inline(stripped[3:]), S_H2))
            story.append(HRFlowable(width="100%", thickness=0.6, color=GREY_LINE, spaceAfter=6))
            i += 1
            continue

        if stripped.startswith("### "):
            story.append(Paragraph(inline(stripped[4:]), S_H3))
            i += 1
            continue

        numbered = NUMBERED_RE.match(stripped)
        if numbered:
            story.append(Paragraph(f"{numbered.group(1)}. {inline(numbered.group(2))}", S_BULLET))
            i += 1
            continue

        if stripped.startswith("- ") or stripped.startswith("* "):
            item = stripped[2:]
            bullet = "•"
            story.append(Paragraph(f"{bullet} {inline(item)}", S_BULLET))
            i += 1
            continue

        if not stripped:
            story.append(Spacer(1, 4))
            i += 1
            continue

        story.append(Paragraph(inline(stripped), S_BODY))
        i += 1

    return story


def main():
    md_text = sanitize(SRC.read_text(encoding="utf-8"))
    doc = SimpleDocTemplate(
        str(OUT), pagesize=A4,
        leftMargin=1.8 * cm, rightMargin=1.8 * cm, topMargin=1.6 * cm, bottomMargin=1.6 * cm,
        title="Réponse — Questions API app agent",
        author="NovAcces / SigasAcces",
    )
    story = build_flowables(md_text)
    doc.build(story)
    print(f"OK -> {OUT}")


if __name__ == "__main__":
    main()
