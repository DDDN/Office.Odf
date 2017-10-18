﻿/*
DDDN.Office.Odf.Odt.OdtConvert
Copyright(C) 2017 Lukasz Jaskiewicz(lukasz @jaskiewicz.de)
- This program is free software; you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation; version 2 of the License.
- This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU General Public License for more details.
- You should have received a copy of the GNU General Public License along with this program; if not, write
to the Free Software Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DDDN.Office.Odf.Odt
{
	public class OdtConvert : IOdtConvert
	{
		private readonly IOdtFile OdtFile;
		private readonly XDocument ContentXDoc;
		private readonly XDocument StylesXDoc;
		private readonly List<OdfEmbedContent> EmbedContent = new List<OdfEmbedContent>();
		private List<OdfStyle> Styles;

		public OdtConvert(IOdtFile odtFile)
		{
			OdtFile = odtFile ?? throw new ArgumentNullException(nameof(odtFile));

			ContentXDoc = OdtFile.GetZipArchiveEntryAsXDocument("content.xml");
			StylesXDoc = OdtFile.GetZipArchiveEntryAsXDocument("styles.xml");
		}

		public OdtConvertData Convert(OdtConvertSettings convertSettings)
		{
			GetOdfStyles();
			var (width, height) = GetPageInfo();

			return new OdtConvertData
			{
				Css = RenderCss(convertSettings),
				Html = GetHtml(convertSettings),
				PageHeight = width,
				PageWidth = height,
				DocumentFirstHeader = GetFirstHeaderText(),
				DocumentFirstParagraph = GetFirstParagraphText(),
				EmbedContent = EmbedContent
			};
		}

		private void GetOdfStyles()
		{
			Styles = new List<OdfStyle>();

			var stylesAutomaticStyles = StylesXDoc.Root
				  .Elements(XName.Get(OdfStyleType.AutomaticStyles, OdfXmlNamespaces.Office))
				  .Elements();
			OdfStylesWalker(stylesAutomaticStyles, Styles);

			var stylesStyles = StylesXDoc.Root
				  .Elements(XName.Get(OdfStyleType.Styles, OdfXmlNamespaces.Office))
				  .Elements();
			OdfStylesWalker(stylesStyles, Styles);

			var contentAutomaticStyles = ContentXDoc.Root
				  .Elements(XName.Get(OdfStyleType.AutomaticStyles, OdfXmlNamespaces.Office))
				  .Elements();
			OdfStylesWalker(contentAutomaticStyles, Styles);

			var contentStyles = ContentXDoc.Root
				  .Elements(XName.Get(OdfStyleType.Styles, OdfXmlNamespaces.Office))
				  .Elements();
			OdfStylesWalker(contentStyles, Styles);

			TransformLevelsToStyles();
		}

		private void TransformLevelsToStyles()
		{
			var levelStyles = new List<OdfStyle>();

			foreach (var style in Styles.Where(p => p.Levels.Count > 0))
			{
				var levelCount = 1;

				foreach (var level in style.Levels)
				{
					var odfStyle = new OdfStyle()
					{
						Name = $"{style.Name}LVL{levelCount}",
						Attributes = style.Levels[levelCount]
					};

					levelStyles.Add(odfStyle);
					levelCount++;
				}
			}

			Styles.AddRange(levelStyles);
		}

		private void HtmlNodesWalker(
			IEnumerable<XNode> odNodes,
			XElement htmlParent,
			OdtConvertSettings convertSettings,
			int level = 0,
			string levelParentClassName = "")
		{
			foreach (var node in odNodes)
			{
				if (node.NodeType == XmlNodeType.Text)
				{
					var textNode = node as XText;
					htmlParent.SetValue(htmlParent.Value + textNode.Value);
				}
				else if (node.NodeType == XmlNodeType.Element)
				{
					var elementNode = node as XElement;

					if (elementNode.Name.Equals(XName.Get("s", OdfXmlNamespaces.Text)))
					{
						AddNbsp(elementNode, htmlParent);
					}
					else if (OdtTrans
						.Tags
						.Exists(p => p.OdfName.Equals(elementNode.Name.LocalName, StringComparison.InvariantCultureIgnoreCase)))
					{
						var htmlTag = OdtTrans.Tags.Find(p => p
							.OdfName
							.Equals(elementNode.Name.LocalName, StringComparison.InvariantCultureIgnoreCase))?.HtmlName;
						var htmlElement = new XElement(htmlTag);
						htmlParent.Add(htmlElement);
						CopyAttributes(elementNode, htmlElement, convertSettings, level, levelParentClassName);
						AddInlineStyles(htmlTag, htmlElement);

						var isLevelParentEle = elementNode.Name.Equals(XName.Get("list", OdfXmlNamespaces.Text));

						if (isLevelParentEle)
						{
							if (level == 0)
							{
								levelParentClassName = elementNode
								.Attributes(XName.Get("style-name", OdfXmlNamespaces.Text))
								.FirstOrDefault()?.Value;
							}

							level++;
						}

						HtmlNodesWalker(elementNode.Nodes(), htmlElement, convertSettings, level, levelParentClassName);

						if (isLevelParentEle)
						{
							level--;
						}
					}
				}
			}
		}

		private void CopyAttributes(
			XElement odElement,
			XElement htmlElement,
			OdtConvertSettings convertSettings,
			int level = 0,
			string levelParentClassName = "")
		{
			if (level > 0 && htmlElement.Name.LocalName.Equals("ul", StringComparison.InvariantCultureIgnoreCase))
			{
				var classAttr = new XAttribute("class", levelParentClassName);
				htmlElement.Add(classAttr);
			}

			foreach (var attr in odElement.Attributes())
			{
				var attrName = $"{odElement.Name.LocalName}.{attr.Name.LocalName}";

				if (OdtTrans.Attrs.TryGetValue(attrName, out string htmlAttrName))
				{
					var attrVal = attr.Value;

					if (htmlAttrName.Equals("src", StringComparison.InvariantCultureIgnoreCase))
					{
						attrVal = GetEmbedContent(attr.Value, convertSettings);
					}

					var htmlAttr = new XAttribute(htmlAttrName, attrVal);
					htmlElement.Add(htmlAttr);
				}
				else if (OdtTrans.StyleAttr.TryGetValue(attrName, out string styleAttrName))
				{
					var styleAttr = htmlElement.Attributes().FirstOrDefault(p => p.Name.LocalName.Equals("style", StringComparison.InvariantCultureIgnoreCase));
					var attrVal = $"{styleAttrName}:{attr.Value};";

					if (styleAttr == default(XAttribute))
					{
						var htmlAttr = new XAttribute("style", attrVal);
						htmlElement.Add(htmlAttr);
					}
					else
					{
						styleAttr.Value += attrVal;
					}
				}
			}

			if (level > 0
				&& htmlElement.Parent.Name.LocalName.Equals("ul", StringComparison.InvariantCultureIgnoreCase)
				&& htmlElement.Name.LocalName.Equals("li", StringComparison.InvariantCultureIgnoreCase))
			{
				var classAttr = htmlElement.Attributes(XName.Get("class")).FirstOrDefault();
				var className = $"{levelParentClassName}LVL{level}";

				if (classAttr == default(XAttribute))
				{
					classAttr = new XAttribute("class", className);
					htmlElement.Add(classAttr);
				}
				else
				{
					classAttr.Value = $"{className} {classAttr.Value}";
				}
			}
		}

		private void OdfStylesWalker(IEnumerable<XElement> elements, List<OdfStyle> styles)
		{
			foreach (var ele in elements)
			{
				var nameAttr = ele.Attributes()
					.FirstOrDefault(p => p.Name.LocalName.Equals("name", StringComparison.InvariantCultureIgnoreCase))?
					.Value;

				var odfStyle = styles.Find(p => 0 == string.Compare(p.Name, nameAttr, StringComparison.InvariantCultureIgnoreCase));

				if (odfStyle == default(OdfStyle)
					|| ele.Name.LocalName.Equals(OdfStyleType.DefaultStyle, StringComparison.InvariantCultureIgnoreCase))
				{
					odfStyle = new OdfStyle
					{
						Type = ele.Name.LocalName,
					};

					styles.Add(odfStyle);
					AttributeWalker(ele, odfStyle);
					ChildWalker(ele.Elements(), odfStyle);
				}
			}
		}

		private void ChildWalker(IEnumerable<XElement> elements, OdfStyle odfStyle, int level = 0)
		{
			foreach (var ele in elements)
			{
				var levelAttr = ele
					.Attributes()
					.FirstOrDefault(p => p.Name.LocalName.Equals("level", StringComparison.InvariantCultureIgnoreCase));

				if (levelAttr == default(XAttribute))
				{
					AttributeWalker(ele, odfStyle, level);
					ChildWalker(ele.Elements(), odfStyle, level);
				}
				else
				{
					var levelNo = int.Parse(levelAttr.Value);
					AttributeWalker(ele, odfStyle, levelNo);
					ChildWalker(ele.Elements(), odfStyle, levelNo);
				}
			}
		}

		private void AttributeWalker(XElement element, OdfStyle odfStyle, int level = 0)
		{
			foreach (var eleAttr in element.Attributes())
			{
				if (level == 0
					&& HandleSpecialAttr(eleAttr, odfStyle))
				{
					continue;
				}

				var newAttr = new OdfStyleAttr()
				{
					Type = element.Name.LocalName,
					Name = eleAttr.Name.LocalName,
					Value = eleAttr.Value
				};

				if (level == 0)
				{
					odfStyle.Attributes.Add(newAttr);
				}
				else
				{
					if (odfStyle.Levels.ContainsKey(level))
					{
						odfStyle.Levels[level].Add(newAttr);
					}
					else
					{
						var attrList = new List<OdfStyleAttr>
						{
							newAttr
						};

						odfStyle.Levels.Add(level, attrList);
					}
				}
			}
		}

		private string RenderCss(OdtConvertSettings convertSettings)
		{
			var builder = new StringBuilder(8192);

			foreach (var style in Styles)
			{
				if (style.Type.Equals(OdfStyleType.DefaultStyle, StringComparison.InvariantCultureIgnoreCase)
					  && OdtTrans.Tags.Exists(p => p.OdfName.Equals(style.Family, StringComparison.InvariantCultureIgnoreCase)))
				{
					builder
						.Append(Environment.NewLine)
						.Append(convertSettings.RootHtmlTag)
						.Append(" ")
						.Append(OdtTrans.Tags.Find(p => p.OdfName.Equals(style.Family, StringComparison.InvariantCultureIgnoreCase)).HtmlName)
						.Append(" {")
						.Append(Environment.NewLine);
					StyleAttrWalker(builder, style);
					builder.Append("}");
				}
				else if (!string.IsNullOrWhiteSpace(style.Name))
				{
					builder.Append(Environment.NewLine)
						.Append(".")
						.Append(style.Name)
						.Append(" {")
						.Append(Environment.NewLine);
					StyleAttrWalker(builder, style);
					builder.Append("}");
				}
			}

			return builder.ToString();
		}

		private void StyleAttrWalker(StringBuilder builder, OdfStyle style)
		{
			if (!string.IsNullOrWhiteSpace(style.ParentStyleName))
			{
				var parentStyle = Styles
					.Find(p => 0 == string.Compare(p.Name, style.ParentStyleName, StringComparison.CurrentCultureIgnoreCase));

				if (parentStyle != default(OdfStyle))
				{
					StyleAttrWalker(builder, parentStyle);
				}
			}

			foreach (var attr in style.Attributes)
			{
				TransformStyleAttr(builder, attr);
			}
		}

		private static void TransformStyleAttr(StringBuilder builder, OdfStyleAttr attr)
		{
			var trans = OdtTrans.Css.Find(p => p.OdfName.Equals(attr.Name, StringComparison.InvariantCultureIgnoreCase));

			if (trans != default(OdfStyleToCss))
			{
				if (string.IsNullOrWhiteSpace(trans.CssName))
				{
					return;
				}
				else
				{
					var attrName = trans.CssName;
					var attrVal = "";

					if (trans.Values?.ContainsKey(attr.Value) == true)
					{
						attrVal = trans.Values[attr.Value];
					}
					else
					{
						attrVal = attr.Value;
					}

					builder
						.Append(attrName)
						.Append(": ")
						.Append(attrVal)
						.Append(";")
						.Append(Environment.NewLine);
				}
			}
			else
			{
				builder
					.Append(attr.Name)
					.Append(": ")
					.Append(attr.Value)
					.Append(";")
					.Append(Environment.NewLine);
			}
		}

		private string GetHtml(OdtConvertSettings convertSettings)
		{
			var documentNodes = ContentXDoc.Root
					  .Elements(XName.Get("body", OdfXmlNamespaces.Office))
					  .Elements(XName.Get("text", OdfXmlNamespaces.Office))
					  .Nodes();

			var rootElement = new XElement
			(
				convertSettings.RootHtmlTag,
				new XAttribute("class", convertSettings.RootHtmlTagClassNames ?? ""),
				new XAttribute("id", convertSettings.RootHtmlTagId ?? "")
			);

			HtmlNodesWalker(documentNodes, rootElement, convertSettings);

			return rootElement.ToString(SaveOptions.DisableFormatting);
		}

		private void AddInlineStyles(string htmlTag, XElement childHtmlEle)
		{
			if (htmlTag.Equals("img", StringComparison.InvariantCultureIgnoreCase))
			{
				var htmlStyleAttr = childHtmlEle.Attributes().FirstOrDefault(p => p.Name.LocalName.Equals("style", StringComparison.InvariantCultureIgnoreCase));

				if (htmlStyleAttr == default(XAttribute))
				{
					htmlStyleAttr = new XAttribute("style", "max-width: 100%; height: auto;");
					childHtmlEle.Add(htmlStyleAttr);
				}
				else
				{
					var styles = htmlStyleAttr.Value.Split(';');

					for (int i = 0; i < styles.Length; i++)
					{
						styles[i] = styles[i].Trim();
						var nameVal = styles[i].Split(':');
						nameVal[0] = nameVal[0].Trim();

						if (nameVal[0].Equals("width", StringComparison.InvariantCultureIgnoreCase))
						{
							styles[i] = "max-width:100%";
						}
						else if (nameVal[0].Equals("height", StringComparison.InvariantCultureIgnoreCase))
						{
							styles[i] = "height:auto";
						}
					}

					htmlStyleAttr.Value = "";

					foreach (var style in styles)
					{
						htmlStyleAttr.Value += $"{style};";
					}
				}
			}
		}

		private static void AddNbsp(XElement odElement, XElement htmlElement)
		{
			var spacesValue = odElement.Attribute(XName.Get("c", OdfXmlNamespaces.Text))?.Value;
			int.TryParse(spacesValue, out int spacesCount);

			if (spacesCount == 0)
			{
				spacesCount++;
			}

			for (int i = 0; i < spacesCount; i++)
			{
				htmlElement.SetValue(htmlElement.Value + "&nbsp;");
			}
		}

		private string GetEmbedContent(string link, OdtConvertSettings convertSettings)
		{
			var id = Guid.NewGuid();
			var name = $"{convertSettings.LinkUrlPrefix}/{id.ToString()}/{link.Replace('/', '_')}";
			var fileContent = OdtFile.GetZipArchiveEntryFileContent(link);

			var content = new OdfEmbedContent
			{
				Id = id,
				Name = name,
				Content = fileContent
			};

			EmbedContent.Add(content);

			return name;
		}

		private bool HandleSpecialAttr(XAttribute attr, OdfStyle odfStyle)
		{
			if (attr.Name.LocalName.Equals("name"))
			{
				odfStyle.Name = attr.Value;
			}
			else if (attr.Name.LocalName.Equals("parent-style-name"))
			{
				odfStyle.ParentStyleName = attr.Value;
			}
			else if (attr.Name.LocalName.Equals("family"))
			{
				odfStyle.Family = attr.Value;
			}
			else if (attr.Name.LocalName.Equals("list-style-name"))
			{
				odfStyle.ListStyleName = attr.Value;
			}
			else
			{
				return false;
			}

			return true;
		}

		private (string width, string height) GetPageInfo()
		{
			var pageLayoutStyle = Styles.Find(p => p.Type.Equals(OdfStyleType.PageLayout, StringComparison.InvariantCultureIgnoreCase));

			if (pageLayoutStyle == null)
			{
				return (null, null);
			}
			else
			{
				var width = pageLayoutStyle.Attributes.Find(p => p.Name
				.Equals("page-width", StringComparison.InvariantCultureIgnoreCase)
				&& p.Type.Equals(OdfStyleType.PageLayoutProperties, StringComparison.InvariantCultureIgnoreCase))
				.Value;
				var height = pageLayoutStyle.Attributes.Find(p => p.Name
				.Equals("page-height", StringComparison.InvariantCultureIgnoreCase)
				&& p.Type.Equals(OdfStyleType.PageLayoutProperties, StringComparison.InvariantCultureIgnoreCase))
				.Value;
				return (width, height);
			}
		}

		private string GetFirstHeaderText()
		{
			var headers = ContentXDoc.Root
				 .Elements(XName.Get("body", OdfXmlNamespaces.Office))
				 .Elements(XName.Get("text", OdfXmlNamespaces.Office))
				 .Elements(XName.Get("h", OdfXmlNamespaces.Text));

			foreach (var h in headers)
			{
				var inner = OdtReader.GetValue(h);
				if (!string.IsNullOrWhiteSpace(inner))
				{
					return inner;
				}
			}

			return "";
		}

		private string GetFirstParagraphText()
		{
			var paragraphs = ContentXDoc.Root
				 .Elements(XName.Get("body", OdfXmlNamespaces.Office))
				 .Elements(XName.Get("text", OdfXmlNamespaces.Office))
				 .Elements(XName.Get("p", OdfXmlNamespaces.Text));

			foreach (var p in paragraphs)
			{
				var inner = OdtReader.GetValue(p);
				if (!string.IsNullOrWhiteSpace(inner))
				{
					return inner;
				}
			}

			return "";
		}
	}
}
