# FLEURS test recordings

The WAV files in this folder are five utterances from the Bulgarian (`bg_bg`) **dev** split of the
**FLEURS** dataset by Google, converted from 32-bit to 16-bit PCM (16 kHz, mono) with SoX. The audio content is unchanged.

* Source: https://huggingface.co/datasets/google/fleurs (`data/bg_bg/audio/dev.tar.gz`, `data/bg_bg/dev.tsv`)
* License: **Creative Commons Attribution 4.0 International (CC BY 4.0)** — https://creativecommons.org/licenses/by/4.0/
* Citation: Conneau, A. et al. (2022). *FLEURS: Few-shot Learning Evaluation of Universal Representations of Speech.* arXiv:2205.12446.

`transcripts.tsv` maps each file to its original FLEURS file name and reference transcription.
They are used only by the automated speech-recognition test (real human Bulgarian speech).
