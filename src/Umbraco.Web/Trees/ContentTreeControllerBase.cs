﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Web.Http;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Persistence;
using Umbraco.Web.Models.Trees;
using Umbraco.Web.WebApi.Filters;
using umbraco;
using umbraco.BusinessLogic.Actions;
using System.Globalization;

namespace Umbraco.Web.Trees
{
    public abstract class ContentTreeControllerBase : TreeController
    {

        #region Actions

        /// <summary>
        /// Gets an individual tree node
        /// </summary>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        [HttpQueryStringFilter("queryStrings")]
        public TreeNode GetTreeNode(string id, FormDataCollection queryStrings)
        {
            int asInt;
            if (int.TryParse(id, out asInt) == false)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));
            }

            var entity = Services.EntityService.Get(asInt, UmbracoObjectType);
            if (entity == null)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));
            }

            var node = GetSingleTreeNode(entity, entity.ParentId.ToInvariantString(), queryStrings);

            //add the tree alias to the node since it is standalone (has no root for which this normally belongs)
            node.AdditionalData["treeAlias"] = TreeAlias;
            return node;
        }

        #endregion

        protected abstract TreeNode GetSingleTreeNode(IUmbracoEntity e, string parentId, FormDataCollection queryStrings);

        /// <summary>
        /// Returns the 
        /// </summary>
        protected abstract int RecycleBinId { get; }

        /// <summary>
        /// Returns true if the recycle bin has items in it
        /// </summary>
        protected abstract bool RecycleBinSmells { get; }

        /// <summary>
        /// Returns the user's start node for this tree
        /// </summary>
        protected abstract int UserStartNode { get; }

        protected abstract TreeNodeCollection PerformGetTreeNodes(string id, FormDataCollection queryStrings);

        protected abstract MenuItemCollection PerformGetMenuForNode(string id, FormDataCollection queryStrings);

        protected abstract UmbracoObjectTypes UmbracoObjectType { get; }

        protected IEnumerable<IUmbracoEntity> GetChildEntities(string id)
        {
            int iid;
            if (int.TryParse(id, out iid) == false)
            {
                throw new InvalidCastException("The id for the media tree must be an integer");
            }

            //if a request is made for the root node data but the user's start node is not the default, then
            // we need to return their start node data
            if (iid == Constants.System.Root && UserStartNode != Constants.System.Root)
            {
                //just return their single start node, it will show up under the 'Content' label
                var startNode = Services.EntityService.Get(UserStartNode, UmbracoObjectType);
                if (startNode == null)
                {
                    throw new EntityNotFoundException(UserStartNode, "User's start content node could not be found");
                }
                return new[] { startNode };
            }

            return Services.EntityService.GetChildren(iid, UmbracoObjectType).ToArray();
        }

        /// <summary>
        /// Returns true or false if the current user has access to the node based on the user's allowed start node (path) access
        /// </summary>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        protected abstract bool HasPathAccess(string id, FormDataCollection queryStrings);

        /// <summary>
        /// This will automatically check if the recycle bin needs to be rendered (i.e. its the first level)
        /// and will automatically append it to the result of GetChildNodes.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        protected sealed override TreeNodeCollection GetTreeNodes(string id, FormDataCollection queryStrings)
        {
            //check if we're rendering the root
            if (id == Constants.System.Root.ToInvariantString())
            {
                //when rendering the root, 3 things can happen:
                //1. we return -1 children without modifications
                //2. the user has a non -1 content root set and we return that
                //3. the tree has a non -1 content root set and we return that - if the user has access to it.

                var hasUserRoot = UserStartNode != Constants.System.Root;
                var hasTreeRoot = queryStrings.HasKey(TreeQueryStringParameters.StartNodeId);

                //initial id
                var idToLoad = id;

                //user permission override root
                if (hasUserRoot)
                    idToLoad = UserStartNode.ToString(CultureInfo.InvariantCulture);

                //tree overrides root
                if (hasTreeRoot)
                {
                    //but only if the user is allowed to access this node
                    var altId = queryStrings.GetValue<string>(TreeQueryStringParameters.StartNodeId);

                    //so if we dont have a user content root or the user has access
                    if (hasUserRoot == false || HasPathAccess(altId, queryStrings))
                    {
                        idToLoad = altId;
                    }
                }

                //load whatever root nodes we concluded was the user/tree root
                var nodes = GetTreeNodesInternal(idToLoad, queryStrings);                

                //only render the recycle bin if we are not in dialog and the start id is still the root
                if (IsDialog(queryStrings) == false && idToLoad == Constants.System.Root.ToInvariantString())
                {
                    nodes.Add(CreateTreeNode(
                        RecycleBinId.ToInvariantString(),
                        idToLoad,
                        queryStrings,
                        ui.GetText("general", "recycleBin"),
                        "icon-trash",
                        RecycleBinSmells,
                        queryStrings.GetValue<string>("application") + TreeAlias.EnsureStartsWith('/') + "/recyclebin"));
                }

                return nodes;
            }

            return GetTreeNodesInternal(id, queryStrings);
        }

        /// <summary>
        /// Before we make a call to get the tree nodes we have to check if they can actually be rendered
        /// </summary>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        /// <remarks>
        /// Currently this just checks if it is a container type, if it is we cannot render children. In the future this might check for other things.
        /// </remarks>
        private TreeNodeCollection GetTreeNodesInternal(string id, FormDataCollection queryStrings)
        {
            //before we get the children we need to see if this is a container node
            var current = Services.EntityService.Get(int.Parse(id), UmbracoObjectType);

            //test if the parent is a listview / container
            if (current != null && current.IsContainer())
            {
                //no children!
                return new TreeNodeCollection();
            }

            return PerformGetTreeNodes(id, queryStrings);
        }

        /// <summary>
        /// Checks if the menu requested is for the recycle bin and renders that, otherwise renders the result of PerformGetMenuForNode
        /// </summary>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        protected sealed override MenuItemCollection GetMenuForNode(string id, FormDataCollection queryStrings)
        {
            if (RecycleBinId.ToInvariantString() == id)
            {
                var menu = new MenuItemCollection();
                menu.Items.Add<ActionEmptyTranscan>(ui.Text("actions", "emptyTrashcan"));
                menu.Items.Add<ActionRefresh>(ui.Text("actions", ActionRefresh.Instance.Alias), true);
                return menu;
            }
            return PerformGetMenuForNode(id, queryStrings);
        }

        /// <summary>
        /// Based on the allowed actions, this will filter the ones that the current user is allowed
        /// </summary>
        /// <param name="menuWithAllItems"></param>
        /// <param name="userAllowedMenuItems"></param>
        /// <returns></returns>
        protected void FilterUserAllowedMenuItems(MenuItemCollection menuWithAllItems, IEnumerable<MenuItem> userAllowedMenuItems)
        {
            var userAllowedActions = userAllowedMenuItems.Where(x => x.Action != null).Select(x => x.Action).ToArray();

            var notAllowed = menuWithAllItems.Items.Where(
                a => (a.Action != null
                      && a.Action.CanBePermissionAssigned
                      && (a.Action.CanBePermissionAssigned == false || userAllowedActions.Contains(a.Action) == false)))
                                             .ToArray();

            //remove the ones that aren't allowed.
            foreach (var m in notAllowed)
            {
                menuWithAllItems.Items.Remove(m);
            }
        }

        internal IEnumerable<MenuItem> GetAllowedUserMenuItemsForNode(IUmbracoEntity dd)
        {
            var actions = global::umbraco.BusinessLogic.Actions.Action.FromString(UmbracoUser.GetPermissions(dd.Path));

            // A user is allowed to delete their own stuff
            if (dd.CreatorId == UmbracoUser.Id && actions.Contains(ActionDelete.Instance) == false)
                actions.Add(ActionDelete.Instance);

            return actions.Select(x => new MenuItem(x));
        }

        /// <summary>
        /// Determins if the user has access to view the node/document
        /// </summary>
        /// <param name="doc">The Document to check permissions against</param>
        /// <param name="allowedUserOptions">A list of MenuItems that the user has permissions to execute on the current document</param>
        /// <remarks>By default the user must have Browse permissions to see the node in the Content tree</remarks>
        /// <returns></returns>        
        internal bool CanUserAccessNode(IUmbracoEntity doc, IEnumerable<MenuItem> allowedUserOptions)
        {
            return allowedUserOptions.Select(x => x.Action).OfType<ActionBrowse>().Any();
        }
    }
}