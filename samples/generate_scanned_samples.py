"""Generate fictitious, scanned-looking PDFs for the batch OCR demo.

Each page is drawn as an image (text, lines, a stamp), slightly rotated, with
scanner noise, then binarised to 1 bit like an office scanner in black and white
mode and saved as an image-only PDF: the files contain no text layer at all.

Every company, person, address and figure is invented.

Usage:
    python generate_scanned_samples.py [output_folder]
"""

import random
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

DPI = 300
PAGE_W, PAGE_H = int(8.5 * DPI), int(11 * DPI)  # US Letter
MARGIN = int(0.9 * DPI)
FOOTER = "Fictitious sample generated for a Nutrient batch OCR demo."

FONT_CANDIDATES = {
    "regular": [
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ],
    "bold": [
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    ],
    "serif": [
        "/System/Library/Fonts/Supplemental/Times New Roman.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSerif-Regular.ttf",
        "C:/Windows/Fonts/times.ttf",
    ],
}


def font(kind, pt):
    size = int(pt * DPI / 72)
    for path in FONT_CANDIDATES[kind]:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default(size=size)


class Page:
    def __init__(self):
        self.img = Image.new("L", (PAGE_W, PAGE_H), 255)
        self.d = ImageDraw.Draw(self.img)
        self.y = MARGIN

    def text(self, s, kind="regular", pt=11, x=None, gap=0.45):
        f = font(kind, pt)
        self.d.text((MARGIN if x is None else x, self.y), s, font=f, fill=0)
        self.y += int(pt * DPI / 72 * (1 + gap))

    def paragraph(self, s, kind="serif", pt=11.5, width=None):
        f = font(kind, pt)
        width = width or PAGE_W - 2 * MARGIN
        line = ""
        for word in s.split():
            trial = (line + " " + word).strip()
            if self.d.textlength(trial, font=f) > width:
                self.text(line, kind, pt)
                line = word
            else:
                line = trial
        if line:
            self.text(line, kind, pt)
        self.y += int(pt * DPI / 72 * 0.5)

    def rule(self, thickness=4):
        self.d.rectangle([MARGIN, self.y, PAGE_W - MARGIN, self.y + thickness], fill=0)
        self.y += int(0.15 * DPI)

    def space(self, inches):
        self.y += int(inches * DPI)

    def table(self, rows, widths, pt=10.5, header=True):
        f, fb = font("regular", pt), font("bold", pt)
        row_h = int(pt * DPI / 72 * 2.0)
        x0 = MARGIN
        total = sum(widths)
        for r, row in enumerate(rows):
            self.d.rectangle([x0, self.y, x0 + total, self.y + row_h], outline=0, width=3)
            x = x0
            for c, cell in enumerate(row):
                if c:
                    self.d.line([x, self.y, x, self.y + row_h], fill=0, width=3)
                ff = fb if header and r == 0 else f
                tx = x + 18
                if c and r > 0 and re.fullmatch(r"[0-9.,\s-]+", cell):
                    tx = x + widths[c] - 18 - self.d.textlength(cell, font=ff)
                self.d.text((tx, self.y + row_h * 0.22), cell, font=ff, fill=0)
                x += widths[c]
            self.y += row_h
        self.y += int(0.2 * DPI)

    def footer(self, n, total):
        f = font("regular", 8)
        self.d.text((MARGIN, PAGE_H - int(0.6 * DPI)), FOOTER, font=f, fill=0)
        label = f"Page {n} of {total}"
        self.d.text((PAGE_W - MARGIN - self.d.textlength(label, font=f), PAGE_H - int(0.6 * DPI)), label, font=f, fill=0)

    def scanned(self, rng):
        """Rotate slightly, add noise and binarise, like a black and white office scan."""
        img = self.img.rotate(rng.uniform(-0.8, 0.8), resample=Image.BICUBIC, fillcolor=255, expand=False)
        img = img.filter(ImageFilter.GaussianBlur(0.8))
        px = img.load()
        for _ in range(9000):  # dust specks
            x, y = rng.randrange(PAGE_W), rng.randrange(PAGE_H)
            px[x, y] = 0
        return img.point(lambda v: 255 if v > 150 else 0).convert("1")


def letterhead(p, company, tagline):
    p.text(company, "bold", 18, gap=0.2)
    p.text(tagline, "regular", 9)
    p.rule()


def invoice():
    p = Page()
    letterhead(p, "Example Office Supplies Ltd", "12 Sample Road, Exampleville  |  accounts@example.com  |  +1 555 0100")
    p.space(0.2)
    p.text("INVOICE", "bold", 16)
    for line in ["Invoice number: INV-2026-0417", "Invoice date: October 12, 2026", "Due date: November 11, 2026",
                 "Customer: Sample Retail Inc., 400 Placeholder Avenue, Anytown"]:
        p.text(line, "regular", 11)
    p.space(0.2)
    p.table([
        ["Item", "Description", "Qty", "Unit price", "Amount"],
        ["A-100", "Copy paper, A4, 80 g, box of 5 reams", "12", "24.50", "294.00"],
        ["B-220", "Ballpoint pens, blue, pack of 50", "4", "18.90", "75.60"],
        ["C-310", "Lever arch files, assorted colours", "20", "3.75", "75.00"],
        ["D-045", "Toner cartridge, black, high yield", "3", "89.00", "267.00"],
        ["E-512", "Desk organiser, mesh, 5 compartments", "6", "14.20", "85.20"],
    ], [int(1.0 * DPI), int(3.2 * DPI), int(0.6 * DPI), int(1.0 * DPI), int(0.9 * DPI)])
    for line in ["Subtotal: 796.80", "Sales tax (8%): 63.74", "Total due: 860.54"]:
        p.text(line, "bold" if "Total" in line else "regular", 11, x=PAGE_W - MARGIN - int(2.6 * DPI))
    p.space(0.3)
    p.paragraph("Payment terms: 30 days net. Please quote the invoice number with your payment. "
                "Goods remain the property of Example Office Supplies Ltd until paid in full.")
    p.paragraph("Thank you for your business.")
    return [p]


def letter():
    pages = []
    p = Page()
    letterhead(p, "Example Utilities Co.", "Customer Service Center  |  1 Demo Plaza, Sampletown  |  www.example.com")
    p.space(0.3)
    for line in ["October 20, 2026", "", "Ms. Jane Example", "77 Placeholder Street", "Anytown", "",
                 "Account number: 0000-1234-5678", "Subject: Changes to your service plan"]:
        p.text(line, "serif", 11.5)
    p.space(0.2)
    p.text("Dear Ms. Example,", "serif", 11.5)
    p.space(0.1)
    body = [
        "We are writing to let you know about changes to your service plan that will take effect on January 1, 2027. "
        "These changes follow our annual review of operating costs and the feedback we received from customers over the past year.",
        "Your monthly service charge will move from 42.00 to 44.50. The rate per unit of energy used will stay the same, and the "
        "discount for paperless billing will increase from 1.50 to 2.00 per month. If you already receive paperless bills, the "
        "new discount will be applied automatically.",
        "We are also extending the hours of our customer service center. From January, our advisors will be available from "
        "8:00 am to 8:00 pm on weekdays and from 9:00 am to 1:00 pm on Saturdays. You can reach them by phone or through the "
        "secure messaging service on our website.",
        "If you would like to discuss these changes or review the options available to you, please contact us at any time. "
        "You may also switch to a different plan without any fee before March 31, 2027.",
    ]
    for b in body:
        p.paragraph(b)
    pages.append(p)

    p = Page()
    p.text("Account number: 0000-1234-5678", "serif", 10)
    p.space(0.2)
    p.text("Summary of changes", "bold", 13)
    p.table([
        ["Item", "Current", "From January 1, 2027"],
        ["Monthly service charge", "42.00", "44.50"],
        ["Paperless billing discount", "1.50", "2.00"],
        ["Late payment fee", "5.00", "5.00"],
        ["Weekday service hours", "9 am - 5 pm", "8 am - 8 pm"],
    ], [int(3.0 * DPI), int(1.6 * DPI), int(2.1 * DPI)])
    for b in [
        "Your rights. You can end your contract at any time with thirty days notice. No exit fee applies if you end your "
        "contract because of these changes, provided you tell us before March 31, 2027.",
        "Thank you for being our customer. We look forward to continuing to serve you.",
    ]:
        p.paragraph(b)
    p.space(0.3)
    p.text("Sincerely,", "serif", 11.5)
    p.space(0.3)
    p.text("Alex Sample", "serif", 11.5)
    p.text("Customer Service Manager, Example Utilities Co.", "serif", 11.5)
    pages.append(p)
    return pages


def report():
    pages = []
    sections = [
        ("1. Summary", [
            "This report covers the scheduled maintenance carried out at the Example Business Park between July 1 and "
            "September 30, 2026. All planned inspections were completed. Two items require follow-up work in the next quarter.",
            "The total maintenance cost for the quarter was 18,420.00, which is 6 percent below the approved budget of 19,600.00.",
        ]),
        ("2. Heating and ventilation", [
            "The four rooftop air handling units were inspected on July 14. Filters were replaced on all units and the belts on "
            "unit 3 were adjusted. The heat recovery wheel on unit 2 shows wear on the seals and should be replaced before winter.",
            "Boiler efficiency tests gave results between 91 and 93 percent, within the expected range for the installed equipment.",
        ]),
        ("3. Electrical systems", [
            "Thermal imaging of the main distribution boards found no hot spots. Emergency lighting was tested for the full "
            "three hour duration in all buildings. Six fittings in building C failed the test and were replaced on August 2.",
        ]),
        ("4. Fire safety", [
            "The fire alarm system was tested weekly. The annual service of the sprinkler pumps was completed on August 21 "
            "with no defects. Fire doors on the second floor of building B need new closers; the work is scheduled for October.",
        ]),
        ("5. Grounds and parking", [
            "Line markings in the main car park were renewed in September. Three trees near the north entrance were pruned "
            "following the arborist inspection. Drainage channels were cleared before the autumn rains.",
        ]),
        ("6. Follow-up actions", [
            "Replace the heat recovery wheel seals on air handling unit 2 (estimated cost 2,150.00, due November 15, 2026).",
            "Install new door closers on the second floor of building B (estimated cost 960.00, due October 31, 2026).",
            "Repeat the emergency lighting test in building C after the replacement fittings have been in service for one month.",
        ]),
    ]
    p = Page()
    letterhead(p, "Example Facilities Services", "Quarterly maintenance report  |  Q3 2026  |  Reference FM-2026-Q3")
    p.space(0.2)
    for i, (title, paras) in enumerate(sections):
        if i == 3:
            pages.append(p)
            p = Page()
        p.text(title, "bold", 13)
        for para in paras:
            p.paragraph(para)
    p.space(0.2)
    p.table([
        ["Category", "Budget", "Actual"],
        ["Heating and ventilation", "7,200.00", "6,880.00"],
        ["Electrical systems", "4,100.00", "3,940.00"],
        ["Fire safety", "3,800.00", "3,650.00"],
        ["Grounds and parking", "4,500.00", "3,950.00"],
        ["Total", "19,600.00", "18,420.00"],
    ], [int(3.2 * DPI), int(1.6 * DPI), int(1.6 * DPI)])
    pages.append(p)
    return pages


def build(folder: Path):
    folder.mkdir(parents=True, exist_ok=True)
    rng = random.Random(42)
    for name, pages in [("invoice-0417.pdf", invoice()), ("service-letter-1020.pdf", letter()),
                        ("maintenance-report-q3.pdf", report())]:
        images = []
        for i, p in enumerate(pages, start=1):
            p.footer(i, len(pages))
            images.append(p.scanned(rng))
        out = folder / name
        images[0].save(out, save_all=True, append_images=images[1:], resolution=DPI)
        print(f"Wrote {out} ({len(images)} page(s), {out.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    build(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent)
