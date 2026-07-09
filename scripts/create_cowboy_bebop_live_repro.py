#!/usr/bin/env python3
"""Create a mock library that resembles the Cowboy Bebop live-output flicker case."""

from __future__ import annotations

import argparse
from pathlib import Path


ALBUM_FOLDER_BASE = "2002 - Cowboy Bebop - CD-BOX Original Soundtrack"
ALBUM_FOLDER_SUFFIX = " [FLAC]"
LEFT_TO_RIGHT_MARK = "\u200e"

DISC_FILES: dict[str, list[str]] = {
    "Disc 1": [
        "01- Dialogue 1-1.flac",
        "02- Tank!.flac",
        "03- Dialoge 1-2.flac",
        "04- Want It All Back.flac",
        "05- Sax Quartet.flac",
        "06- Dialogue 1-3.flac",
        "07- Encore un Verre.flac",
        "08- March for Koala.flac",
        "09- Dialogue 1-4.flac",
        "10- Felt Tip Pen.flac",
        "11- The Egg and You.flac",
        "12- Dialogue 1-5.flac",
        "13- Pot City.flac",
        "14- Dialogue 1-6.flac",
        "15- NY Rush.flac",
        "16- Dialogue 1-7.flac",
        "17- Fe.flac",
        "18- Piano Black.flac",
        "19- Dialogue 1-8.flac",
        "20- Spokey Dorkey.flac",
        "21- Forever Broke.flac",
        "22- Dialogue 1-9.flac",
        "23- Road to the West.flac",
        "24- Dialogue 1-10.flac",
        "25- Meteor.flac",
        "26- Dialogue 1-11.flac",
        "27- Digging My Potato.flac",
        "28- Dialogue 1-12.flac",
        "29- Rain.flac",
        "30- Dialogue 1-13.flac",
        "31- Green Bird.flac",
        "00 Playlist.m3u",
        "Cowboy Bebop Original Soundtrack CD Box (Disc 1).cue",
        "folder.jpg",
    ],
    "Disc 2": [
        "01- Dialogue 2-01.flac",
        "02- Cats on Mars.flac",
        "03- Doggy Dog II.flac",
        "04- Doggy Dog III.flac",
        "05- Dialogue 2-02.flac",
        "06- Piano Bar I.flac",
        "07- Give and Take.flac",
        "08- Dialogue 2-03.flac",
        "09- Cat Blues.flac",
        "10- Dialogue 2-04.flac",
        "11- The Singing Sea II.flac",
        "12- Dialogue 2-05.flac",
        "13- Elm.flac",
        "14- Waltz for Zizi.flac",
        "15- Dialogue 2-06.flac",
        "16- Kawaisou na Faye (High Socks).flac",
        "17- Farewell Blues (Alternate Take).flac",
        "18- Dialogue 2-07.flac",
        "19- Words That we Couldn't Say.flac",
        "20- Dialogue 2-08.flac",
        "21- Space Lion (Orgel Version).flac",
        "22- Waste Land.flac",
        "23- Dialogue 2-09.flac",
        "24- Goodnight Julia.flac",
        "25- Space Lion.flac",
        "00 Playlist.m3u",
        "Cowboy Bebop Original Soundtrack CD Box (Disc 2).cue",
        "folder.jpg",
    ],
    "Disc 3": [
        "01- Dialogue 3-01.flac",
        "02- Go Go Cactus (Guitar Version).flac",
        "03- Dialogue 3-02.flac",
        "04- Too Good Too Bad.flac",
        "05- Dialogue 3-03.flac",
        "06- Eyeball.flac",
        "07- Dialogue 3-04.flac",
        "08- Yuuenchi (Amusement Park).flac",
        "09- On the Run.flac",
        "10- Dialogue 3-05.flac",
        "11- Episode 23 (Dialogue Added).flac",
        "12- Dialogue 3-06.flac",
        "13- Don't Bother None (Long Version).flac",
        "14- Dialogue 3-07.flac",
        "15- Wo Qui Non Coin.flac",
        "16- Kawaisou na Faye (Lip Cream).flac",
        "17- Call Me Call Me.flac",
        "18- Dialogue 3-08.flac",
        "19- Memory.flac",
        "20- Adieu (Long Version).flac",
        "21- Dialogue 3-09.flac",
        "22- See You Space Cowboys.flac",
        "23- Dialogue 3-10.flac",
        "24- Blue.flac",
        "00 Playlist.m3u",
        "Cowboy Bebop Original Soundtrack CD Box (Disc 3).cue",
        "folder.jpg",
    ],
    "Disc 4": [
        "01- Tank! (Live).flac",
        "02- Rush (Live).flac",
        "03- What Planet is This (Live).flac",
        "04- Too Good Too Bad (Live).flac",
        "05- Bad Dog No Biscuit (Live).flac",
        "06- Call Me Call Me (Live).flac",
        "07- Mushroom Hunting (Live).flac",
        "08- The Real Folk Blues (Live).flac",
        "09- Piano Solo (Live).flac",
        "10- Ask DNA.flac",
        "11- SF Game Center.flac",
        "12- Rouya.flac",
        "13- Old School Game.flac",
        "00 Playlist.m3u",
        "Cowboy Bebop Original Soundtrack CD Box (Disc 4).cue",
        "folder.jpg",
    ],
    "Disc 5": [
        "01- Wandering Cowboy (ED).flac",
        "02- Fascinating Horse Ride (Andy).flac",
        "03- Wandering Cowboy (Ein).flac",
        "00 Playlist.m3u",
        "Cowboy Bebop Original Soundtrack CD Box (Disc 5).cue",
        "folder.jpg",
    ],
}

BOOKLET_FILES = [
    *(f"Book-Page-{i:02d}.jpg" for i in range(1, 13)),
    "Box-Back.jpg",
    "Box-Front.jpg",
    "box_cowboy_bebop_-_01_-_boite_face_avant.jpg",
    "box_cowboy_bebop_-_02_-_boite_face_arriere.jpg",
    "box_cowboy_bebop_-_03_-_boite_tranche.jpg",
    "box_cowboy_bebop_-_06_-_box_face_avant.jpg",
    "box_cowboy_bebop_-_07_-_box_face_arriere.jpg",
    "box_cowboy_bebop_-_09_-_etiquette.jpg",
    "box_cowboy_bebop_-_10_-_cd1.jpg",
    "box_cowboy_bebop_-_11_-_cd2.jpg",
    "box_cowboy_bebop_-_12_-_cd3.jpg",
    "box_cowboy_bebop_-_12_-_cd4.jpg",
    "box_cowboy_bebop_-_14_-_livret_page_01_-_face_avant.jpg",
    "box_cowboy_bebop_-_15_-_livret_page_02.jpg",
    "box_cowboy_bebop_-_16_-_livret_page_05.jpg",
    "box_cowboy_bebop_-_17_-_livret_page_12.jpg",
    "box_cowboy_bebop_-_18_-_livret_page_23.jpg",
    "box_cowboy_bebop_-_19_-_livret_page_26.jpg",
    "box_cowboy_bebop_-_20_-_livret_page_27.jpg",
    "box_cowboy_bebop_-_21_-_livret_page_32.jpg",
    "box_cowboy_bebop_-_22_-_livret_page_33.jpg",
    "box_cowboy_bebop_-_23_-_livret_page_40.jpg",
    "box_cowboy_bebop_-_24_-_livret_page_41.jpg",
    "box_cowboy_bebop_-_25_-_livret_page_42.jpg",
    "box_cowboy_bebop_-_26_-_livret_page_43.jpg",
    "box_cowboy_bebop_-_27_-_livret_page_44.jpg",
    "box_cowboy_bebop_-_28_-_livret_page_45.jpg",
    "box_cowboy_bebop_-_29_-_livret_page_46.jpg",
    "box_cowboy_bebop_-_30_-_livret_page_47.jpg",
    "box_cowboy_bebop_-_31_-_livret_page_48.jpg",
    "box_cowboy_bebop_-_32_-_livret_page_49.jpg",
    "box_cowboy_bebop_-_33_-_livret_page_57.jpg",
    "box_cowboy_bebop_-_34_-_livret_page_59.jpg",
    "box_cowboy_bebop_-_35_-_livret_page_60_-_face_arriere.jpg",
    "box_cowboy_bebop_single_-_00_-_blister.jpg",
    "box_cowboy_bebop_single_-_01_-_front.jpg",
    "box_cowboy_bebop_single_-_02_-_lyrics.jpg",
    "box_cowboy_bebop_single_-_03_-_cd.jpg",
    "box_cowboy_bebop_single_-_04_-_back.jpg",
]


def create_sparse_file(path: Path, size_bytes: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as handle:
        handle.truncate(size_bytes)


def generated_size_bytes(relative_path: str) -> int:
    name = Path(relative_path).name.lower()
    if name.endswith(".flac"):
        return (8 + (sum(relative_path.encode("utf-8")) % 25)) * 1024 * 1024
    if name.endswith(".jpg"):
        return (256 + (sum(relative_path.encode("utf-8")) % 2048)) * 1024
    return 16 * 1024


def iter_relative_paths() -> list[str]:
    paths: list[str] = []
    for disc, files in DISC_FILES.items():
        paths.extend(f"{disc}\\{filename}" for filename in files)
    paths.extend(f"booklet\\{filename}" for filename in BOOKLET_FILES)
    return paths


def generate(output: Path, include_lrm: bool) -> None:
    library_dir = output / "mock-library"
    csv_dir = output / "csv"
    out_dir = output / "out"
    csv_dir.mkdir(parents=True, exist_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)

    album_folder = ALBUM_FOLDER_BASE + (LEFT_TO_RIGHT_MARK if include_lrm else "") + ALBUM_FOLDER_SUFFIX
    album_root = library_dir / "MUSIC" / "Cowboy Bebop" / album_folder

    relative_paths = iter_relative_paths()
    for relative_path in relative_paths:
        create_sparse_file(album_root / Path(relative_path), generated_size_bytes(relative_path))

    query_file = csv_dir / "cowboy_bebop_album.csv"
    query_file.write_text("artist,title,album\nCowboy Bebop,,Cowboy Bebop\n", encoding="utf-8")

    has_lrm = "yes" if include_lrm else "no"
    print(f"Created library: {library_dir}")
    print(f"Created album folder: {album_root}")
    print(f"Created query CSV: {query_file}")
    print(f"Created output directory: {out_dir}")
    print(f"Files: {len(relative_paths)} ({has_lrm} U+200E left-to-right mark in album folder)")
    print()
    print("Example commands:")
    print(f'  sockseek "cowboy bebop" --mock-files-dir "{library_dir}" --mock-files-no-read-tags --mock-files-slow -g -o "{out_dir}"')
    print(f'  sockseek "{query_file}" --mock-files-dir "{library_dir}" --mock-files-no-read-tags --mock-files-slow -o "{out_dir}"')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        required=True,
        help="Output directory for the generated repro library.",
    )
    parser.add_argument(
        "--without-lrm",
        action="store_true",
        help="Generate the same folder without the U+200E mark before [FLAC].",
    )
    args = parser.parse_args()

    generate(args.output, include_lrm=not args.without_lrm)


if __name__ == "__main__":
    main()
