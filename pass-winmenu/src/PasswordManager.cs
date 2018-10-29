﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PassWinmenu.ExternalPrograms;
using PassWinmenu.Utilities;
using PassWinmenu.Utilities.ExtensionMethods;

namespace PassWinmenu
{
	internal class DecryptedPasswordFile : PasswordFile
	{
		public string Content => $"{Password}\n{Metadata}";

		public string Password { get; }
		public string Metadata { get; }

		public DecryptedPasswordFile(string relativePath, string password, string metaData) : base(relativePath)
		{
			Password = password;
			Metadata = metaData;
		}
	}

	internal class PasswordFile
	{
		public string FullPath => Path.GetFullPath(RelativePath);
		public string RelativePath { get; }
		public string Name => Path.GetFileName(RelativePath);

		public PasswordFile(string relativePath)
		{
			RelativePath = relativePath;
		}
	}

	internal class PasswordManager
	{
		internal const string GpgIdFileName = ".gpg-id";

		private readonly PinentryWatcher pinentryWatcher = new PinentryWatcher();
		private readonly DirectoryInfo passwordStoreDirectory;

		public ICryptoService Crypto { get; }
		public bool PinentryFixEnabled { get; set; }
		public readonly string EncryptedFileExtension;

		public PasswordManager(string passwordStore, string encryptedFileExtension, GPG crypto)
		{
			var normalised = Helpers.NormaliseDirectory(passwordStore);
			passwordStoreDirectory = new DirectoryInfo(normalised);

			EncryptedFileExtension = encryptedFileExtension;
			Crypto = crypto;
		}

		/// <summary>
		/// Generates an encrypted password file at the specified path.
		/// If the path contains directories that do not exist, they will be created automatically.
		/// </summary>
		/// <param name="file">
		/// A <see cref="DecryptedPasswordFile"/> instance specifying the contents
		/// of the password file to be generated.
		/// </param>
		public void EncryptPassword(DecryptedPasswordFile file)
		{
			EncryptText(file.Content, file.RelativePath);
		}

		/// <summary>
		/// Generates an ecrypted password file at the specified path.
		/// If the path contains directories that do not exist, they will be created automatically.
		/// </summary>
		/// <param name="text">The text to be encrypted.</param>
		/// <param name="path">A relative path specifying where in the password store the password file should be generated.</param>
		public void EncryptText(string text, string path)
		{
			var fullPath = GetPasswordFilePath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
			Crypto.Encrypt(text, fullPath, GetGpgIds(fullPath));
		}

		public string DecryptText(string path)
		{
			var fullPath = GetPasswordFilePath(path);
			if (!File.Exists(fullPath)) throw new ArgumentException($"The password file \"{fullPath}\" does not exist.");

			if(PinentryFixEnabled) pinentryWatcher.BumpPinentryWindow();
			return Crypto.Decrypt(fullPath);
		}

		/// <summary>
		/// Get the content from an encrypted password file.
		/// </summary>
		/// <param name="path">A relative path specifying the location of the password file in the password store.</param>
		/// <param name="passwordOnFirstLine">Should be true if the first line of the file contains the password.
		/// Any content in the remaining lines will be considered metadata.
		/// If set to false, the contents of the entire file are considered to be the password.</param>
		/// <returns></returns>
		public DecryptedPasswordFile DecryptPassword(string path, bool passwordOnFirstLine)
		{
			var content = DecryptText(path);
			var file = new PasswordFile(path);
			return new PasswordFileParser().Parse(file, content, !passwordOnFirstLine);
		}

		/// <summary>
		/// Encrypt a file. The path to the unencrypted file (plus a .gpg extension)
		/// is used to produce the path to the encrypted file.
		/// </summary>
		/// <param name="file">A relative path pointing to the encrypted file in the password store.</param>
		public PasswordFile EncryptFile(string file)
		{
			var fullFilePath = GetPasswordFilePath(file);
			if (!File.Exists(fullFilePath)) throw new ArgumentException($"The unencrypted file \"{fullFilePath}\" does not exist.");
			Crypto.EncryptFile(fullFilePath, fullFilePath + EncryptedFileExtension, GetGpgIds(file));

			return new PasswordFile(file + EncryptedFileExtension);
		}

		/// <summary>
		/// Decrypt a file. The encrypted file should have a .gpg extension, which
		/// is removed to produce the path to the decrypted file.
		/// </summary>
		/// <param name="path">The path, relative to the password store, to the encrypted file.</param>
		/// <returns>An absolute path pointing to the decrypted file.</returns>
		public string DecryptFile(string path)
		{
			if (!path.EndsWith(EncryptedFileExtension))
			{
				throw new ArgumentException($"The encrypted file \"{path}\" should have a filename ending with {EncryptedFileExtension}");
			}

			var encryptedFileName = GetPasswordFilePath(path);
			var decryptedFileName = encryptedFileName.Substring(0, encryptedFileName.Length - EncryptedFileExtension.Length);

			if (File.Exists(decryptedFileName)) throw new InvalidOperationException($"A plaintext file already exists at \"{decryptedFileName}\".");
			if (!File.Exists(encryptedFileName)) throw new ArgumentException($"The encrypted file \"{encryptedFileName}\" does not exist.");

			if(PinentryFixEnabled) pinentryWatcher.BumpPinentryWindow();
			Crypto.DecryptToFile(encryptedFileName, decryptedFileName);
			return decryptedFileName;
		}

		/// <summary>
		/// Turns a relative (password store) path into an absolute path pointing to a password store file.
		/// Throws an exception if the given path is not in the password store.
		/// </summary>
		/// <param name="relativePath">A relative path pointing to a file or directory in the password store.</param>
		/// <returns>The absolute path of that file or directory.</returns>
		public string GetPasswordFilePath(string relativePath)
		{
			// Ensure the directory separators are correct
			var normalised = Helpers.NormaliseDirectory(relativePath);
			// Only allow saving encrypted files to the password store
			if (Path.IsPathRooted(relativePath))
			{
				// The given path is absolute. If it's a child of the password directory, that's fine.
				// If it isn't, throw an error.
				if (passwordStoreDirectory.IsParentOf(relativePath))
				{
					return relativePath;
				}
				else
				{
					throw new ArgumentException("Password file path must be relative to the password store directory.");
				}
			}
			var fullPath = Path.Combine(passwordStoreDirectory.FullName, normalised);
			return fullPath;
		}

		/// <summary>
		/// Returns all password files that match a search pattern.
		/// </summary>
		/// <param name="pattern">The pattern against which the files should be matched.</param>
		/// <returns></returns>
		public IEnumerable<string> GetPasswordFiles(string pattern)
		{
			var files = Directory.EnumerateFiles(passwordStoreDirectory.FullName, "*", SearchOption.AllDirectories);
			var matchingFiles = files.Where(f => Regex.IsMatch(Path.GetFileName(f), pattern));
			var relativeNames = matchingFiles.Select(p => Helpers.GetRelativePath(p, passwordStoreDirectory.FullName));

			return relativeNames;
		}

		/// <summary>
		/// Searches the given path for a gpg-id file.
		/// </summary>
		/// <param name="path">The path that should be searched. This path does not have to point to an
		/// existing file or directory, but it must be located within the password store.</param>
		/// <returns>An array of GPG ids taken from the first gpg-id file that is encountered,
		/// or null if no gpg-id file was found.</returns>
		private string[] GetGpgIds(string path)
		{
			// Ensure the path does not contain any trailing slashes or AltDirectorySeparatorChars
			path = Helpers.NormaliseDirectory(path);

			// Ensure the password file directory is actually located within the password store.
			if (!passwordStoreDirectory.IsParentOf(path))
			{
				throw new ArgumentException("The given directory is not a subdirectory of the password store.");
			}

			// Walk up from the innermost directory, and keep moving up until an existing directory 
			// containing a gpg-id file is found.
			var current = new DirectoryInfo(path);
			while (!current.Exists || !current.ContainsFile(GpgIdFileName))
			{
				if (current.Parent == null || current.PathEquals(passwordStoreDirectory))
				{
					return null;
				}
				current = current.Parent;
			}

			return File.ReadAllLines(Path.Combine(current.FullName, GpgIdFileName));
		}
	}
}
