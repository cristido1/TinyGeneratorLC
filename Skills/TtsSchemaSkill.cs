using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using Microsoft.AspNetCore.Routing.Constraints;

namespace TinyGenerator.Skills
{
    public class TtsSchemaSkill
    {
        private readonly string _storyText;          // Immutabile
        private readonly string _workingFolder;            // Percorso file per salvataggio schema
        private TtsSchema _schema;                   // Struttura di lavoro dell'agente

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ------------------------------------------------------------
        // COSTRUTTORE
        // ------------------------------------------------------------
        public TtsSchemaSkill(string storyText, string workingFolder)
        {
            _storyText = storyText;
            _schema = new TtsSchema();
            _workingFolder = workingFolder;
        }

        // ------------------------------------------------------------
        // LETTURA STORIA
        // ------------------------------------------------------------
        [KernelFunction, Description("Restituisce la storia completa in testo semplice.")]
        public string ReadStoryText() => _storyText;

        // ------------------------------------------------------------
        // RESET SCHEMA
        // ------------------------------------------------------------
        [KernelFunction, Description("Azzera completamente lo schema TTS.")]
        public string ResetSchema()
        {
            _schema = new TtsSchema();
            return "OK";
        }

        // ------------------------------------------------------------
        // PERSONAGGI
        // ------------------------------------------------------------
        [KernelFunction, Description("Aggiunge un personaggio allo schema.")]
        public string AddCharacter(string name, string voice, string gender, string emotionDefault)
        {
            _schema.Characters.Add(new TtsCharacter
            {
                Name = name,
                Voice = voice,
                Gender = gender,
                EmotionDefault = emotionDefault
            });

            return "OK";
        }

        [KernelFunction, Description("Rimuove un personaggio dallo schema.")]
        public string DeleteCharacter(string name)
        {
            _schema.Characters.RemoveAll(c => c.Name == name);
            return "OK";
        }

        // ------------------------------------------------------------
        // FRASI
        // ------------------------------------------------------------
        [KernelFunction, Description("Aggiunge una frase pronunciata da un personaggio.")]
        public string AddPhrase(string character, string text, string emotion)
        {
            _schema.Timeline.Add(new TtsPhrase
            {
                Character = character,
                Text = text,
                Emotion = emotion
            });

            return "OK";
        }

        // ------------------------------------------------------------
        // PAUSE
        // ------------------------------------------------------------
        [KernelFunction, Description("Aggiunge una pausa di un certo numero di secondi.")]
        public string AddPause(int seconds)
        {
            if (seconds < 1) seconds = 1;

            _schema.Timeline.Add(new TtsPause(seconds));

            return "OK";
        }

        // ------------------------------------------------------------
        // DELETE LAST ENTRY (frase o pausa)
        // ------------------------------------------------------------
        [KernelFunction, Description("Cancella l'ultima frase o pausa aggiunta.")]
        public string DeleteLast()
        {
            if (_schema.Timeline.Count == 0) 
                return "EMPTY";

            _schema.Timeline.RemoveAt(_schema.Timeline.Count - 1);
            return "OK";
        }

        // ------------------------------------------------------------
        // SERIALIZZAZIONE
        // ------------------------------------------------------------
        [KernelFunction, Description("Salva lo schema TTS su un file JSON.")]
        public string ConfirmSchema()
        {
            try
            {
                string filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, JsonOptions));
                return "OK";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        // ------------------------------------------------------------
        // CHECK SCHEMA
        // ------------------------------------------------------------
        [KernelFunction, Description("Verifica che lo schema TTS sia valido.")]
        public string CheckSchema()
        {
            if (_schema.Characters.Count == 0)
                return "ERROR: No characters";

            if (_schema.Timeline.Count == 0)
                return "ERROR: No timeline entries";

            if (string.IsNullOrWhiteSpace(_storyText))
                return "ERROR: Story empty";

            return "OK";
        }
    }
}